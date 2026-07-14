[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CandidatePath,

    [string]$LiveLibraryPath = "$HOME\Music\iTunes\iTunes Library.itl",

    [string]$DumpItlExe,

    [int]$ExpectedTrackCount = -1,

    [int]$TimeoutSeconds = 120,

    [string]$RunName,

    [ValidateSet('None', 'CreatePlaylist', 'CreatePlaylistsToCount', 'DeletePlaylist', 'SetFirstTrackName', 'SetFirstTrackBookmark', 'SetFirstTrackPlayCount', 'PlayFirstTrackAtPosition', 'ImportFile', 'ImportFilesToCount', 'DeleteFirstTrack')]
    [string]$Experiment = 'None',

    [string]$ExperimentValue,

    [int]$ExperimentTargetCount = -1,

    [switch]$ManualChooser,

    [switch]$MultiPhase
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
            $libraryPath = [IO.Path]::GetFullPath($libraryPath)
            Set-Clipboard -Value $libraryPath
            Write-Host "Choose this library in iTunes: $libraryPath"
            Write-Host 'The full library path has been copied to the clipboard.'
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

function Start-ItunesSession([string]$libraryPath, [switch]$ChooseLibrary, [switch]$Manual) {
    if ($ChooseLibrary) {
        Open-ItunesLibraryChooser $libraryPath -Manual:$Manual
    } else {
        [void](Start-Process -FilePath $itunesExe -PassThru)
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Get-Process iTunes -ErrorAction SilentlyContinue)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for the iTunes process to start.' }
        Start-Sleep -Milliseconds 250
    }

    $application = New-Object -ComObject iTunes.Application
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($null -eq $application.LibraryPlaylist -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $application.LibraryPlaylist) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($application)
        throw 'Timed out waiting for iTunes to open the disposable library.'
    }
    return $application
}

function Stop-ItunesSession([object]$application) {
    $application.Quit()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($application)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (Get-Process iTunes -ErrorAction SilentlyContinue) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for iTunes to exit.' }
        Start-Sleep -Milliseconds 250
    }
}

function Find-ItunesPlaylist([object]$application, [string]$name) {
    $playlists = $application.LibrarySource.Playlists
    try {
        for ($index = 1; $index -le $playlists.Count; $index++) {
            $playlist = $playlists.Item($index)
            if ($playlist.Name -eq $name) { return $playlist }
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($playlist)
        }
        return $null
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($playlists)
    }
}

function Invoke-ItunesExperiment(
    [object]$application,
    [string]$mode,
    [string]$value,
    [Collections.IDictionary]$state,
    [switch]$Reverse
) {
    if ($Reverse) {
        switch ($mode) {
            'CreatePlaylist' {
                $playlist = Find-ItunesPlaylist $application $value
                if ($null -eq $playlist) { throw "Playlist '$value' was not found for reversal." }
                $playlist.Delete()
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($playlist)
                Write-Host "Reversed playlist creation for '$value'."
            }
            'CreatePlaylistsToCount' {
                foreach ($name in @($state.CreatedPlaylistNames)) {
                    $playlist = Find-ItunesPlaylist $application $name
                    if ($null -eq $playlist) { throw "Playlist '$name' was not found for reversal." }
                    $playlist.Delete()
                    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($playlist)
                }
                Write-Host "Removed $(@($state.CreatedPlaylistNames).Count) threshold-probe playlists."
            }
            'SetFirstTrackName' {
                $track = $application.LibraryPlaylist.Tracks.Item(1)
                if ($null -eq $track) { throw 'The disposable library has no first track to restore.' }
                $track.Name = [string]$state.OriginalTrackName
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track)
                Write-Host "Restored first track name to '$($state.OriginalTrackName)'."
            }
            'SetFirstTrackBookmark' {
                $track = $application.LibraryPlaylist.Tracks.Item(1)
                if ($null -eq $track) { throw 'The disposable library has no first track to restore.' }
                try {
                    $track.RememberBookmark = $true
                    $track.BookmarkTime = [double]$state.OriginalBookmarkTime
                    $track.RememberBookmark = [bool]$state.OriginalRememberBookmark
                }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track) }
                Write-Host "Restored first track bookmark state."
            }
            'SetFirstTrackPlayCount' {
                $track = $application.LibraryPlaylist.Tracks.Item(1)
                if ($null -eq $track) { throw 'The disposable library has no first track to restore.' }
                try { $track.PlayedCount = [int]$state.OriginalPlayedCount }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track) }
                Write-Host "Restored first track play count to $($state.OriginalPlayedCount)."
            }
            'PlayFirstTrackAtPosition' {
                $track = $application.LibraryPlaylist.Tracks.Item(1)
                if ($null -eq $track) { throw 'The disposable library has no first track to restore.' }
                try {
                    $track.PlayedCount = [int]$state.OriginalPlayedCount
                    $track.RememberBookmark = $true
                    $track.BookmarkTime = [double]$state.OriginalBookmarkTime
                    $track.RememberBookmark = [bool]$state.OriginalRememberBookmark
                }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track) }
                Write-Host 'Restored observable first-track playback properties.'
            }
            'ImportFile' {
                $tracks = $application.LibraryPlaylist.Tracks
                $track = $null
                try {
                    for ($index = 1; $index -le $tracks.Count; $index++) {
                        $candidateTrack = $tracks.Item($index)
                        if ([string]::Equals($candidateTrack.Location, [string]$state.MediaCopy,
                                [StringComparison]::OrdinalIgnoreCase)) {
                            $track = $candidateTrack
                            break
                        }
                        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($candidateTrack)
                    }
                    if ($null -eq $track) { throw "Imported track '$($state.MediaCopy)' was not found for reversal." }
                    $track.Delete()
                    Write-Host "Removed imported disposable media '$($state.MediaCopy)'."
                }
                finally {
                    if ($null -ne $track) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track) }
                    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($tracks)
                }
            }
            'ImportFilesToCount' {
                $locations = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($location in @($state.ImportedTrackLocations)) {
                    [void]$locations.Add([string]$location)
                }
                $removed = 0
                foreach ($location in $locations) {
                    $libraryPlaylist = $application.LibraryPlaylist
                    $tracks = $libraryPlaylist.Tracks
                    $found = $false
                    try {
                        for ($index = $tracks.Count; $index -ge 1; $index--) {
                            $track = $tracks.Item($index)
                            try {
                                if ([string]::Equals([string]$track.Location, $location,
                                        [StringComparison]::OrdinalIgnoreCase)) {
                                    $track.Delete()
                                    $removed++
                                    $found = $true
                                    break
                                }
                            }
                            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track) }
                        }
                    }
                    finally {
                        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($tracks)
                        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($libraryPlaylist)
                    }
                    if (-not $found) {
                        throw "Imported threshold track '$location' was not found for reversal."
                    }
                }
                if ($removed -ne $locations.Count) {
                    throw "Removed $removed of $($locations.Count) imported threshold tracks."
                }
                Write-Host "Removed $removed imported threshold tracks."
            }
            default { throw "Experiment '$mode' does not have a safe automated reversal." }
        }
        return
    }

    switch ($mode) {
        'None' { return }
        'CreatePlaylist' {
            if ([string]::IsNullOrWhiteSpace($value)) { throw 'CreatePlaylist requires -ExperimentValue.' }
            $playlist = $application.CreatePlaylist($value)
            if ($null -eq $playlist) { throw "iTunes did not create playlist '$value'." }
            Write-Host "Created disposable playlist '$value'."
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($playlist)
        }
        'CreatePlaylistsToCount' {
            $target = 0
            if (-not [int]::TryParse($value, [ref]$target) -or $target -lt 1) {
                throw 'CreatePlaylistsToCount requires -ExperimentValue containing a positive total playlist count.'
            }
            $initial = [int]$state.InitialBinaryPlaylistCount
            if ($target -le $initial) {
                throw "Target binary playlist count $target must be greater than the current count $initial."
            }
            $names = [Collections.Generic.List[string]]::new()
            for ($ordinal = 1; $ordinal -le $target - $initial; $ordinal++) {
                $name = 'DumpITL-Threshold-{0:D3}-{1:D3}' -f $target, $ordinal
                $playlist = $application.CreatePlaylist($name)
                if ($null -eq $playlist) { throw "iTunes did not create playlist '$name'." }
                $names.Add($name)
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($playlist)
            }
            $state.CreatedPlaylistNames = $names.ToArray()
            Write-Host "Created $($names.Count) playlists, targeting binary playlist count $target."
        }
        'DeletePlaylist' {
            if ([string]::IsNullOrWhiteSpace($value)) { throw 'DeletePlaylist requires -ExperimentValue.' }
            $playlist = Find-ItunesPlaylist $application $value
            if ($null -eq $playlist) { throw "Playlist '$value' was not found." }
            $playlist.Delete()
            Write-Host "Deleted disposable playlist '$value'."
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($playlist)
        }
        'SetFirstTrackName' {
            if ([string]::IsNullOrWhiteSpace($value)) { throw 'SetFirstTrackName requires -ExperimentValue.' }
            $track = $application.LibraryPlaylist.Tracks.Item(1)
            if ($null -eq $track) { throw 'The disposable library has no first track.' }
            $state.OriginalTrackName = $track.Name
            $track.Name = $value
            Write-Host "Changed first track name '$($state.OriginalTrackName)' -> '$value'."
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track)
        }
        'SetFirstTrackBookmark' {
            $bookmark = 0.0
            if (-not [double]::TryParse($value, [Globalization.NumberStyles]::Float,
                    [Globalization.CultureInfo]::InvariantCulture, [ref]$bookmark) -or $bookmark -lt 0) {
                throw 'SetFirstTrackBookmark requires -ExperimentValue containing non-negative seconds.'
            }
            $track = $application.LibraryPlaylist.Tracks.Item(1)
            if ($null -eq $track) { throw 'The disposable library has no first track.' }
            try {
                $state.OriginalRememberBookmark = [bool]$track.RememberBookmark
                $state.OriginalBookmarkTime = [double]$track.BookmarkTime
                $track.RememberBookmark = $true
                $track.BookmarkTime = $bookmark
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track) }
            Write-Host "Set first track bookmark to $bookmark seconds with RememberBookmark enabled."
        }
        'SetFirstTrackPlayCount' {
            $playCount = 0
            if (-not [int]::TryParse($value, [ref]$playCount) -or $playCount -lt 0) {
                throw 'SetFirstTrackPlayCount requires -ExperimentValue containing a non-negative integer.'
            }
            $track = $application.LibraryPlaylist.Tracks.Item(1)
            if ($null -eq $track) { throw 'The disposable library has no first track.' }
            try {
                $state.OriginalPlayedCount = [int]$track.PlayedCount
                $track.PlayedCount = $playCount
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track) }
            Write-Host "Set first track play count to $playCount."
        }
        'PlayFirstTrackAtPosition' {
            $position = 0.0
            if (-not [double]::TryParse($value, [Globalization.NumberStyles]::Float,
                    [Globalization.CultureInfo]::InvariantCulture, [ref]$position) -or $position -lt 0) {
                throw 'PlayFirstTrackAtPosition requires -ExperimentValue containing non-negative seconds.'
            }
            $track = $application.LibraryPlaylist.Tracks.Item(1)
            if ($null -eq $track) { throw 'The disposable library has no first track.' }
            try {
                $state.OriginalPlayedCount = [int]$track.PlayedCount
                $state.OriginalRememberBookmark = [bool]$track.RememberBookmark
                $state.OriginalBookmarkTime = [double]$track.BookmarkTime
                $track.Play()
                Start-Sleep -Seconds 2
                $application.PlayerPosition = $position
                Start-Sleep -Seconds 2
                $application.Stop()
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track) }
            Write-Host "Played the first track at position $position seconds, then stopped."
        }
        'ImportFile' {
            if ([string]::IsNullOrWhiteSpace($value) -or
                -not (Test-Path -LiteralPath $value -PathType Leaf)) {
                throw 'ImportFile requires -ExperimentValue naming an existing media file.'
            }
            $mediaCopy = Join-Path $runRoot ("experiment-media" + [IO.Path]::GetExtension($value))
            Copy-Item -LiteralPath $value -Destination $mediaCopy
            $state.MediaCopy = [IO.Path]::GetFullPath($mediaCopy)
            $operation = $application.LibraryPlaylist.AddFile($mediaCopy)
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
            while ($operation.InProgress -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 250
            }
            if ($operation.InProgress) { throw 'Timed out waiting for iTunes to import the media file.' }
            Write-Host "Imported disposable media '$mediaCopy'."
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($operation)
        }
        'ImportFilesToCount' {
            if ([string]::IsNullOrWhiteSpace($value) -or -not (Test-Path -LiteralPath $value)) {
                throw 'ImportFilesToCount requires -ExperimentValue naming an existing media file or fixture directory.'
            }
            $initial = [int]$state.InitialBinaryTrackCount
            if ($ExperimentTargetCount -le $initial) {
                throw "Target binary track count $ExperimentTargetCount must be greater than the current count $initial."
            }

            $mediaRoot = Join-Path $runRoot 'threshold-media'
            New-Item -ItemType Directory -Path $mediaRoot -Force | Out-Null
            $locations = [Collections.Generic.List[string]]::new()
            $required = $ExperimentTargetCount - $initial
            if (Test-Path -LiteralPath $value -PathType Container) {
                $sources = @(Get-ChildItem -LiteralPath $value -File | Sort-Object Name |
                    Select-Object -ExpandProperty FullName)
                if ($sources.Count -lt $required) {
                    throw "Fixture directory has $($sources.Count) files; $required are required."
                }
            } else {
                $sources = @($value) * $required
            }
            for ($ordinal = 1; $ordinal -le $required; $ordinal++) {
                $title = 'DumpITL Threshold Track {0:D3}-{1:D3}' -f $ExperimentTargetCount, $ordinal
                $source = [string]$sources[$ordinal - 1]
                $mediaCopy = Join-Path $mediaRoot ($title + [IO.Path]::GetExtension($source))
                Copy-Item -LiteralPath $source -Destination $mediaCopy
                $operation = $application.LibraryPlaylist.AddFile($mediaCopy)
                try {
                    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
                    while ($operation.InProgress -and [DateTime]::UtcNow -lt $deadline) {
                        Start-Sleep -Milliseconds 100
                    }
                    if ($operation.InProgress) { throw "Timed out importing '$mediaCopy'." }
                    $importedTracks = $operation.Tracks
                    try {
                        $track = $importedTracks.Item(1)
                        try { $locations.Add([string]$track.Location) }
                        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track) }
                    }
                    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($importedTracks) }
                }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($operation) }
            }
            $state.ImportedTrackLocations = $locations.ToArray()
            Write-Host "Imported $($locations.Count) tracks, targeting binary track count $ExperimentTargetCount."
        }
        'DeleteFirstTrack' {
            $track = $application.LibraryPlaylist.Tracks.Item(1)
            if ($null -eq $track) { throw 'The disposable library has no first track.' }
            $oldName = $track.Name
            $track.Delete()
            Write-Host "Deleted first disposable track '$oldName'."
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($track)
        }
    }
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
if ($MultiPhase -and $Experiment -notin @('CreatePlaylist', 'CreatePlaylistsToCount', 'SetFirstTrackName', 'SetFirstTrackBookmark', 'SetFirstTrackPlayCount', 'PlayFirstTrackAtPosition', 'ImportFile', 'ImportFilesToCount')) {
    throw "Multi-phase mode requires a reversible experiment: CreatePlaylist, CreatePlaylistsToCount, SetFirstTrackName, SetFirstTrackBookmark, SetFirstTrackPlayCount, PlayFirstTrackAtPosition, ImportFile, or ImportFilesToCount."
}
if ($MultiPhase -and -not (Test-Path -LiteralPath $DumpItlExe -PathType Leaf)) {
    throw "Multi-phase mode requires the DumpITL executable: $DumpItlExe"
}

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
$experimentState = [ordered]@{}
$phaseRecords = [Collections.Generic.List[object]]::new()
$manifestPath = Join-Path $runRoot 'experiment.json'
$runFailure = $null
$guards = [ordered]@{
    liveLibraryHashRestored = $false
    preferencesRestored = $false
    databaseLocationRestored = $false
}

function Write-Utf8Text([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}

function Write-ResearchManifest([string]$status) {
    $manifest = [ordered]@{
        schemaVersion = 1
        status = $status
        runName = $stamp
        candidatePath = $candidate
        disposableLibraryPath = $labLibrary
        experiment = $Experiment
        experimentValue = $ExperimentValue
        experimentTargetCount = $ExperimentTargetCount
        multiPhase = [bool]$MultiPhase
        phases = @($phaseRecords)
        guards = $guards
        error = $runFailure
    }
    Write-Utf8Text $manifestPath (($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
}

function Save-ResearchPhase([string]$name, [string]$previousLibrary) {
    $phaseLibrary = Join-Path $runRoot ($name + '.itl')
    $snapshot = Join-Path $runRoot ($name + '.snapshot.json')
    $validation = Join-Path $runRoot ($name + '.validate.txt')
    Copy-Item -LiteralPath $labLibrary -Destination $phaseLibrary -Force

    $validationLines = @(& $DumpItlExe validate $phaseLibrary 2>&1 | ForEach-Object { $_.ToString() })
    $validationExitCode = $LASTEXITCODE
    Write-Utf8Text $validation (($validationLines -join [Environment]::NewLine) + [Environment]::NewLine)
    if ($validationExitCode -ne 0) { throw "DumpITL validation failed for research phase '$name'." }

    $snapshotMessages = @(& $DumpItlExe snapshot $phaseLibrary $snapshot 2>&1 |
        ForEach-Object { $_.ToString() })
    $snapshotExitCode = $LASTEXITCODE
    if ($snapshotExitCode -ne 0) { throw "DumpITL snapshot failed for research phase '$name'." }
    foreach ($message in $snapshotMessages) { Write-Host $message }

    $comparison = $null
    if (-not [string]::IsNullOrWhiteSpace($previousLibrary)) {
        $previousName = [IO.Path]::GetFileNameWithoutExtension($previousLibrary)
        $comparison = Join-Path $runRoot ($previousName + '-to-' + $name + '.compare.txt')
        $comparisonLines = @(& $DumpItlExe compare $previousLibrary $phaseLibrary 2>&1 |
            ForEach-Object { $_.ToString() })
        $comparisonExitCode = $LASTEXITCODE
        Write-Utf8Text $comparison (($comparisonLines -join [Environment]::NewLine) + [Environment]::NewLine)
        if ($comparisonExitCode -ne 0) { throw "DumpITL comparison failed for research phase '$name'." }
    }

    $phaseRecords.Add([ordered]@{
        name = $name
        library = [IO.Path]::GetFileName($phaseLibrary)
        snapshot = [IO.Path]::GetFileName($snapshot)
        validation = [IO.Path]::GetFileName($validation)
        comparison = if ($null -eq $comparison) { $null } else { [IO.Path]::GetFileName($comparison) }
    })
    Write-ResearchManifest 'running'
    return $phaseLibrary
}

if ($MultiPhase) {
    $baselinePhase = Save-ResearchPhase '00-baseline' $null
    $baselineSnapshot = Get-Content -Raw -LiteralPath (Join-Path $runRoot '00-baseline.snapshot.json') |
        ConvertFrom-Json
    $experimentState.InitialBinaryPlaylistCount = [int]$baselineSnapshot.parsedCounts.playlists
    $experimentState.InitialBinaryTrackCount = [int]$baselineSnapshot.parsedCounts.tracks
} else {
    $baselinePhase = $experimentBefore
}

try {
    Set-LibraryLocation $labLocation
    if ((Get-LibraryLocation) -ne $labLocation) { throw 'iTunes did not retain the disposable library location.' }

    $itunes = Start-ItunesSession $labLibrary -ChooseLibrary -Manual:$ManualChooser

    $actualTracks = $itunes.LibraryPlaylist.Tracks.Count
    Write-Host "iTunes opened disposable library with $actualTracks tracks."
    if ($ExpectedTrackCount -ge 0 -and $actualTracks -ne $ExpectedTrackCount) {
        throw "Expected $ExpectedTrackCount tracks; iTunes reports $actualTracks."
    }

    Invoke-ItunesExperiment $itunes $Experiment $ExperimentValue $experimentState
    Stop-ItunesSession $itunes
    $itunes = $null

    if ($MultiPhase) {
        $mutatedPhase = Save-ResearchPhase '01-mutated' $baselinePhase
        if ($Experiment -eq 'CreatePlaylistsToCount') {
            $mutatedSnapshot = Get-Content -Raw -LiteralPath (Join-Path $runRoot '01-mutated.snapshot.json') |
                ConvertFrom-Json
            if ([int]$mutatedSnapshot.parsedCounts.playlists -ne [int]$ExperimentValue) {
                throw "Expected binary playlist count $ExperimentValue; snapshot reports $($mutatedSnapshot.parsedCounts.playlists)."
            }
        }
        if ($Experiment -eq 'ImportFilesToCount') {
            $mutatedSnapshot = Get-Content -Raw -LiteralPath (Join-Path $runRoot '01-mutated.snapshot.json') |
                ConvertFrom-Json
            if ([int]$mutatedSnapshot.parsedCounts.tracks -ne $ExperimentTargetCount) {
                throw "Expected binary track count $ExperimentTargetCount; snapshot reports $($mutatedSnapshot.parsedCounts.tracks)."
            }
        }

        $itunes = Start-ItunesSession $labLibrary
        Write-Host "Reopened disposable library with $($itunes.LibraryPlaylist.Tracks.Count) tracks."
        Stop-ItunesSession $itunes
        $itunes = $null
        $reopenedPhase = Save-ResearchPhase '02-reopened' $mutatedPhase

        $itunes = Start-ItunesSession $labLibrary
        Invoke-ItunesExperiment $itunes $Experiment $ExperimentValue $experimentState -Reverse
        Stop-ItunesSession $itunes
        $itunes = $null
        [void](Save-ResearchPhase '03-reversed' $reopenedPhase)
        Write-ResearchManifest 'completed'
        Write-Host "Multi-phase research bundle retained at $runRoot"
    }
    elseif (Test-Path -LiteralPath $DumpItlExe -PathType Leaf) {
        & $DumpItlExe validate $labLibrary
        if ($LASTEXITCODE -ne 0) { throw 'DumpITL validation failed after the iTunes re-save.' }
        if ($Experiment -ne 'None') {
            & $DumpItlExe compare $experimentBefore $labLibrary
            if ($LASTEXITCODE -ne 0) { throw 'DumpITL comparison failed after the iTunes experiment.' }
        }
    }

    Write-Host "Acceptance candidate retained at $labLibrary"
}
catch {
    $runFailure = $_.Exception.Message
    if ($MultiPhase) { Write-ResearchManifest 'failed' }
    throw
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
            $guards.liveLibraryHashRestored = $true
            Write-Host 'Live library hash verified unchanged.'
        }
    } catch { $restoreErrors.Add("Unable to restore the live library: $($_.Exception.Message)") }

    try {
        Copy-Item -LiteralPath $preferenceBackup -Destination $preferences -Force
        if ((Get-FileHash -LiteralPath $preferences -Algorithm SHA256).Hash -ne $preferenceHash) {
            $restoreErrors.Add('The iTunes preference file did not restore byte-for-byte.')
        } else {
            $guards.preferencesRestored = $true
            if ((Get-LibraryLocation) -ne $originalLocation) {
                $restoreErrors.Add('The original iTunes Database Location was not restored.')
            } else {
                $guards.databaseLocationRestored = $true
                Write-Host 'iTunes preferences and Database Location restored.'
            }
        }
    } catch { $restoreErrors.Add("Unable to restore iTunes preferences: $($_.Exception.Message)") }

    if ($MultiPhase) {
        $finalStatus = if ($restoreErrors.Count -gt 0) { 'guard-failed' }
            elseif ($null -ne $runFailure) { 'failed' }
            else { 'completed' }
        Write-ResearchManifest $finalStatus
    }

    if ($restoreErrors.Count -gt 0) {
        throw ($restoreErrors -join [Environment]::NewLine)
    }
}
