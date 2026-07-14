[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CandidatePath,

    [string]$LiveLibraryPath = "$HOME\Music\iTunes\iTunes Library.itl",

    [string]$DumpItlExe,

    [int]$ExpectedTrackCount = -1,

    [int]$TimeoutSeconds = 120,

    [string]$RunName,

    [ValidateSet('None', 'CreatePlaylist', 'DeletePlaylist', 'SetFirstTrackName', 'ImportFile', 'DeleteFirstTrack')]
    [string]$Experiment = 'None',

    [string]$ExperimentValue,

    [switch]$ManualChooser
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($DumpItlExe)) {
    $DumpItlExe = Join-Path $PSScriptRoot 'bin\Release\net10.0\DumpITL.exe'
}
$defaultsExe = 'C:\Program Files\iTunes\defaults.exe'
$itunesExe = 'C:\Program Files\iTunes\iTunes.exe'
$preferences = Join-Path $env:APPDATA 'Apple Computer\Preferences\com.apple.iTunes.plist'
$labRoot = 'C:\tmp\DumpITL-acceptance'
$domain = 'com.apple.iTunes'
$key = 'Database Location'

function Get-LibraryLocation {
    $value = (& $defaultsExe read $domain $key 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Unable to read iTunes Database Location: $value" }
    return $value.Trim('"')
}

function Set-LibraryLocation([string]$value) {
    & $defaultsExe write $domain $key -string $value | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unable to set iTunes Database Location." }
}

function ConvertTo-ItunesFileUri([string]$path) {
    $uri = [Uri]::new([IO.Path]::GetFullPath($path)).AbsoluteUri
    return $uri.Replace('file:///', 'file://localhost/')
}

function Open-ItunesLibraryChooser([string]$libraryPath, [switch]$Manual) {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms
    if (-not ('DumpItl.Keyboard' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace DumpItl {
    public static class Keyboard {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        public static void ShiftDown() { keybd_event(0xA0, 0, 0, UIntPtr.Zero); }
        public static void ShiftUp() { keybd_event(0xA0, 0, 2, UIntPtr.Zero); }
    }
}
'@
    }

    [DumpItl.Keyboard]::ShiftDown()
    try {
        $process = Start-Process -FilePath $itunesExe -PassThru
        if ($Manual) {
            Write-Host "Choose this library in iTunes: $libraryPath"
            Start-Sleep -Seconds 4
            return
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $choose = $null
        while ($null -eq $choose -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 200
            $itunesIds = @((Get-Process iTunes -ErrorAction SilentlyContinue).Id)
            $elements = [Windows.Automation.AutomationElement]::RootElement.FindAll(
                [Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.Condition]::TrueCondition)
            foreach ($element in $elements) {
                try {
                    if ($itunesIds -contains $element.Current.ProcessId -and
                        $element.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and
                        $element.Current.Name -like 'Choose Library*') {
                        $choose = $element
                        break
                    }
                } catch { }
            }
        }
        if ($null -eq $choose) { throw 'The iTunes Choose Library window did not appear.' }
    }
    finally {
        [DumpItl.Keyboard]::ShiftUp()
    }

    $invoke = $choose.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    Start-Sleep -Seconds 1
    [Windows.Forms.SendKeys]::SendWait($libraryPath)
    [Windows.Forms.SendKeys]::SendWait('{ENTER}')
}

$candidate = [IO.Path]::GetFullPath($CandidatePath)
$live = [IO.Path]::GetFullPath($LiveLibraryPath)
if ($candidate -eq $live) { throw 'The candidate must not be the live iTunes library.' }
if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Candidate not found: $candidate" }
if (-not (Test-Path -LiteralPath $live -PathType Leaf)) { throw "Live library not found: $live" }
if (-not (Test-Path -LiteralPath $defaultsExe -PathType Leaf)) { throw "iTunes defaults.exe not found: $defaultsExe" }
if (-not (Test-Path -LiteralPath $itunesExe -PathType Leaf)) { throw "iTunes executable not found: $itunesExe" }
if (-not (Test-Path -LiteralPath $preferences -PathType Leaf)) { throw "iTunes preferences not found: $preferences" }
if (Get-Process iTunes -ErrorAction SilentlyContinue) { throw 'Quit iTunes before running acceptance.' }

$stamp = if ([string]::IsNullOrWhiteSpace($RunName)) { Get-Date -Format 'yyyyMMdd-HHmmss' } else { $RunName }
$runRoot = Join-Path $labRoot $stamp
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$labLibrary = Join-Path $runRoot 'iTunes Library.itl'
Copy-Item -LiteralPath $candidate -Destination $labLibrary
$experimentBefore = Join-Path $runRoot 'experiment.before.itl'
Copy-Item -LiteralPath $labLibrary -Destination $experimentBefore

$preferenceBackup = Join-Path $runRoot 'com.apple.iTunes.plist.before'
Copy-Item -LiteralPath $preferences -Destination $preferenceBackup
$liveBackup = Join-Path $runRoot 'live-library.before.itl'
Copy-Item -LiteralPath $live -Destination $liveBackup
$preferenceHash = (Get-FileHash -LiteralPath $preferences -Algorithm SHA256).Hash
$liveHash = (Get-FileHash -LiteralPath $live -Algorithm SHA256).Hash
$originalLocation = Get-LibraryLocation
$labLocation = ConvertTo-ItunesFileUri $labLibrary
$itunes = $null

try {
    Set-LibraryLocation $labLocation
    if ((Get-LibraryLocation) -ne $labLocation) { throw 'iTunes did not retain the disposable library location.' }

    Open-ItunesLibraryChooser $labLibrary -Manual:$ManualChooser
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Get-Process iTunes -ErrorAction SilentlyContinue)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for the iTunes process to start.' }
        Start-Sleep -Milliseconds 250
    }
    $itunes = New-Object -ComObject iTunes.Application
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($null -eq $itunes.LibraryPlaylist -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $itunes.LibraryPlaylist) { throw 'Timed out waiting for iTunes to open the disposable library.' }

    $actualTracks = $itunes.LibraryPlaylist.Tracks.Count
    Write-Host "iTunes opened disposable library with $actualTracks tracks."
    if ($ExpectedTrackCount -ge 0 -and $actualTracks -ne $ExpectedTrackCount) {
        throw "Expected $ExpectedTrackCount tracks; iTunes reports $actualTracks."
    }

    switch ($Experiment) {
        'CreatePlaylist' {
            if ([string]::IsNullOrWhiteSpace($ExperimentValue)) {
                throw 'CreatePlaylist requires -ExperimentValue.'
            }
            $playlist = $itunes.CreatePlaylist($ExperimentValue)
            if ($null -eq $playlist) { throw "iTunes did not create playlist '$ExperimentValue'." }
            Write-Host "Created disposable playlist '$ExperimentValue'."
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($playlist)
        }
        'DeletePlaylist' {
            if ([string]::IsNullOrWhiteSpace($ExperimentValue)) {
                throw 'DeletePlaylist requires -ExperimentValue.'
            }
            $playlists = $itunes.LibrarySource.Playlists
            $playlist = $null
            for ($index = 1; $index -le $playlists.Count; $index++) {
                $candidatePlaylist = $playlists.Item($index)
                if ($candidatePlaylist.Name -eq $ExperimentValue) {
                    $playlist = $candidatePlaylist
                    break
                }
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($candidatePlaylist)
            }
            if ($null -eq $playlist) { throw "Playlist '$ExperimentValue' was not found." }
            $playlist.Delete()
            Write-Host "Deleted disposable playlist '$ExperimentValue'."
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($playlist)
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($playlists)
        }
        'SetFirstTrackName' {
            if ([string]::IsNullOrWhiteSpace($ExperimentValue)) {
                throw 'SetFirstTrackName requires -ExperimentValue.'
            }
            $track = $itunes.LibraryPlaylist.Tracks.Item(1)
            if ($null -eq $track) { throw 'The disposable library has no first track.' }
            $oldName = $track.Name
            $track.Name = $ExperimentValue
            Write-Host "Changed first track name '$oldName' -> '$ExperimentValue'."
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track)
        }
        'ImportFile' {
            if ([string]::IsNullOrWhiteSpace($ExperimentValue) -or
                -not (Test-Path -LiteralPath $ExperimentValue -PathType Leaf)) {
                throw 'ImportFile requires -ExperimentValue naming an existing media file.'
            }
            $mediaCopy = Join-Path $runRoot ("experiment-media" + [IO.Path]::GetExtension($ExperimentValue))
            Copy-Item -LiteralPath $ExperimentValue -Destination $mediaCopy
            $operation = $itunes.LibraryPlaylist.AddFile($mediaCopy)
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
            while ($operation.InProgress -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 250
            }
            if ($operation.InProgress) { throw 'Timed out waiting for iTunes to import the media file.' }
            Write-Host "Imported disposable media '$mediaCopy'."
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($operation)
        }
        'DeleteFirstTrack' {
            $track = $itunes.LibraryPlaylist.Tracks.Item(1)
            if ($null -eq $track) { throw 'The disposable library has no first track.' }
            $oldName = $track.Name
            $track.Delete()
            Write-Host "Deleted first disposable track '$oldName'."
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track)
        }
    }

    $itunes.Quit()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($itunes)
    $itunes = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (Get-Process iTunes -ErrorAction SilentlyContinue) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for iTunes to exit.' }
        Start-Sleep -Milliseconds 250
    }

    if (Test-Path -LiteralPath $DumpItlExe -PathType Leaf) {
        & $DumpItlExe validate $labLibrary
        if ($LASTEXITCODE -ne 0) { throw 'DumpITL validation failed after the iTunes re-save.' }
        if ($Experiment -ne 'None') {
            & $DumpItlExe compare $experimentBefore $labLibrary
            if ($LASTEXITCODE -ne 0) { throw 'DumpITL comparison failed after the iTunes experiment.' }
        }
    }

    Write-Host "Acceptance candidate retained at $labLibrary"
}
finally {
    if ($null -ne $itunes) {
        try { $itunes.Quit() } catch { }
        try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($itunes) } catch { }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Get-Process iTunes -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Process iTunes -ErrorAction SilentlyContinue) {
        Get-Process iTunes -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 1
    }

    $restoreErrors = [Collections.Generic.List[string]]::new()
    try {
        if ((Get-FileHash -LiteralPath $live -Algorithm SHA256).Hash -ne $liveHash) {
            Copy-Item -LiteralPath $liveBackup -Destination $live -Force
            if ((Get-FileHash -LiteralPath $live -Algorithm SHA256).Hash -ne $liveHash) {
                $restoreErrors.Add('The live iTunes library backup did not restore byte-for-byte.')
            } else {
                Write-Warning 'iTunes touched the live library; the harness restored its byte-for-byte backup.'
            }
        }
        if ((Get-FileHash -LiteralPath $live -Algorithm SHA256).Hash -eq $liveHash) {
            Write-Host 'Live library hash verified unchanged.'
        }
    } catch { $restoreErrors.Add("Unable to restore the live library: $($_.Exception.Message)") }

    try {
        Copy-Item -LiteralPath $preferenceBackup -Destination $preferences -Force
        if ((Get-FileHash -LiteralPath $preferences -Algorithm SHA256).Hash -ne $preferenceHash) {
            $restoreErrors.Add('The iTunes preference file did not restore byte-for-byte.')
        } elseif ((Get-LibraryLocation) -ne $originalLocation) {
            $restoreErrors.Add('The original iTunes Database Location was not restored.')
        } else {
            Write-Host 'iTunes preferences and Database Location restored.'
        }
    } catch { $restoreErrors.Add("Unable to restore iTunes preferences: $($_.Exception.Message)") }

    if ($restoreErrors.Count -gt 0) {
        throw ($restoreErrors -join [Environment]::NewLine)
    }
}
