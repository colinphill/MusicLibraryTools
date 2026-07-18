param(
    [string] $Executable = (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows10.0.26100.0\win-x64\MusicLibraryManager.Studio.exe'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\studio-optical\original')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class StudioCaptureNative
{
    public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public static IntPtr FindWebViewChild(IntPtr parent)
    {
        IntPtr renderHost = IntPtr.Zero;
        IntPtr largestChromeChild = IntPtr.Zero;
        long largestArea = 0;
        EnumChildWindows(parent, (window, parameter) =>
        {
            if (!IsWindowVisible(window))
                return true;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            string value = name.ToString();
            if (value == "Chrome_RenderWidgetHostHWND")
                renderHost = window;
            Rect rect;
            if (value.StartsWith("Chrome_", StringComparison.Ordinal) && GetWindowRect(window, out rect))
            {
                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > largestArea)
                {
                    largestArea = area;
                    largestChromeChild = window;
                }
            }
            return true;
        }, IntPtr.Zero);
        return renderHost != IntPtr.Zero ? renderHost : largestChromeChild;
    }
}
'@

function Wait-ForMainWindow([Diagnostics.Process] $Process, [int] $TimeoutSeconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 200
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Studio exited before its main window opened (exit code $($Process.ExitCode))."
        }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $Process.MainWindowHandle
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Studio did not open a main window within $TimeoutSeconds seconds."
}

function Invoke-RelativeClick([IntPtr] $Window, [int] $X, [int] $Y) {
    $windowRect = [StudioCaptureNative+Rect]::new()
    if (-not [StudioCaptureNative]::GetWindowRect($Window, [ref] $windowRect)) {
        throw 'Could not read the Studio window bounds.'
    }
    # PowerShell is DPI-unaware, so GetWindowRect and SetCursorPos use the same virtualized
    # coordinate space. Applying the Studio DPI scale here would scale the click twice.
    $screenX = $windowRect.Left + $X
    $screenY = $windowRect.Top + $Y
    [void] [StudioCaptureNative]::SetForegroundWindow($Window)
    [void] [StudioCaptureNative]::SetCursorPos($screenX, $screenY)
    [StudioCaptureNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [StudioCaptureNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Save-WindowCapture([IntPtr] $Window, [string] $Name) {
    $rect = [StudioCaptureNative+Rect]::new()
    if (-not [StudioCaptureNative]::GetWindowRect($Window, [ref] $rect)) {
        throw 'Could not read the Studio window bounds.'
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = [Drawing.Bitmap]::new($width, $height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $deviceContext = $graphics.GetHdc()
            try {
                if (-not [StudioCaptureNative]::PrintWindow($Window, $deviceContext, 2)) {
                    throw 'Windows could not render the Studio window into the reference bitmap.'
                }
            }
            finally {
                $graphics.ReleaseHdc($deviceContext)
            }
        }
        finally {
            $graphics.Dispose()
        }
        $path = Join-Path $OutputDirectory "$Name.png"
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        $path
    }
    finally {
        $bitmap.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Studio executable not found: $Executable"
}

[void] (New-Item -ItemType Directory -Path $OutputDirectory -Force)
$cursor = [StudioCaptureNative+Point]::new()
[void] [StudioCaptureNative]::GetCursorPos([ref] $cursor)
$previousDestination = [Environment]::GetEnvironmentVariable('STUDIO_CAPTURE_DESTINATION', 'Process')
try {
    foreach ($destination in 'Home', 'Library', 'Health', 'Ingest', 'Organize', 'Operations', 'Settings') {
        $env:STUDIO_CAPTURE_DESTINATION = $destination
        $studioProcess = Start-Process -FilePath $Executable -PassThru -WindowStyle Normal
        try {
            $window = Wait-ForMainWindow $studioProcess
            [void] [StudioCaptureNative]::ShowWindowAsync($window, 9)
            $scale = [StudioCaptureNative]::GetDpiForWindow($window) / 96.0
            $captureWidth = [int] [Math]::Round(1440 * $scale)
            $captureHeight = [int] [Math]::Round(900 * $scale)
            [void] [StudioCaptureNative]::SetWindowPos($window, [IntPtr](-1), 40, 40, $captureWidth, $captureHeight, 0x0040)
            Start-Sleep -Seconds 4
            Save-WindowCapture $window $destination
        }
        finally {
            if (-not $studioProcess.HasExited) {
                [void] $studioProcess.CloseMainWindow()
                if (-not $studioProcess.WaitForExit(5000)) {
                    $studioProcess.Kill($true)
                    $studioProcess.WaitForExit()
                }
            }
            $studioProcess.Dispose()
        }
    }
}
finally {
    [void] [StudioCaptureNative]::SetCursorPos($cursor.X, $cursor.Y)
    [Environment]::SetEnvironmentVariable('STUDIO_CAPTURE_DESTINATION', $previousDestination, 'Process')
}
