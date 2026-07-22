<#
.SYNOPSIS
  Generates the media fixtures used by the MusicFileUtilities tests.

  Run automatically at build time by a target in MusicFileUtilities.Tests.csproj, so the
  binary fixtures are produced by the toolchain rather than committed to the repo.

  The audio formats come from ffmpeg (mostly 0.3s 44.1kHz/16-bit/stereo tone clips tagged with
  a fixed baseline). The .dsf file is hand-crafted here because no DSD encoder is bundled;
  it is a minimal-but-valid DSF container (DSD64, stereo, 1-bit) with no metadata chunk, so
  the DSF tag-write path is exercised by the tests writing tags into it.
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Resolve-Ffmpeg {
    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @('C:\ffmpeg\bin\ffmpeg.exe', 'C:\Program Files\ffmpeg\bin\ffmpeg.exe')) {
        if (Test-Path $p) { return $p }
    }
    throw "ffmpeg not found on PATH or in known locations. Install ffmpeg to generate test fixtures."
}

function Resolve-VorbisEncoderArgs {
    param([Parameter(Mandatory = $true)][string]$Ffmpeg)

    $encoders = (& $Ffmpeg -hide_banner -encoders 2>&1) | Out-String
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed while listing available encoders." }

    if ($encoders -match '(?m)^\s*A\S{5}\s+libvorbis\s') {
        return @('-c:a', 'libvorbis')
    }
    if ($encoders -match '(?m)^\s*A\S{5}\s+vorbis\s') {
        # FFmpeg's built-in Vorbis encoder is marked experimental in current releases.
        return @('-c:a', 'vorbis', '-strict', 'experimental')
    }

    throw "ffmpeg provides neither the libvorbis nor native Vorbis audio encoder."
}

# --- ffmpeg-encoded fixtures ----------------------------------------------------------------
$meta = @(
    '-metadata', 'title=TestTitle',
    '-metadata', 'artist=TestArtist',
    '-metadata', 'album=TestAlbum',
    '-metadata', 'date=2021',
    '-metadata', 'genre=Rock',
    '-metadata', 'track=3'
)
$sine = @('-f', 'lavfi', '-i', 'sine=frequency=440:duration=0.3', '-ac', '2', '-ar', '44100')

$jobs = @(
    @{ File = 'sample.flac';      Args = @('-sample_fmt', 's16') + $meta },
    @{ File = 'sample.mp3';       Args = @('-c:a', 'libmp3lame', '-b:a', '128k', '-write_xing', '1', '-id3v2_version', '3') + $meta },
    @{ File = 'sample.ogg';       Args = $null },
    @{ File = 'sample_alac.m4a';  Args = @('-c:a', 'alac') + $meta },
    @{ File = 'sample_aac.m4a';   Args = @('-c:a', 'aac', '-b:a', '128k') + $meta },
    @{ File = 'sample.wv';        Args = @('-c:a', 'wavpack') + $meta }
)

$needFfmpeg = $jobs | Where-Object { -not (Test-Path (Join-Path $OutDir $_.File)) }
if ($needFfmpeg) {
    $ffmpeg = Resolve-Ffmpeg
    $oggJob = $needFfmpeg | Where-Object { $_.File -eq 'sample.ogg' }
    if ($oggJob) {
        $oggJob.Args = @(Resolve-VorbisEncoderArgs -Ffmpeg $ffmpeg) + $meta
    }
    foreach ($j in $needFfmpeg) {
        $out = Join-Path $OutDir $j.File
        & $ffmpeg -y -hide_banner -loglevel error @sine @($j.Args) $out
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $($j.File)" }
    }
}

# Additional quality/channel fixtures used by ingest policy tests.
$specialJobs = @(
    @{
        File = 'sample_hires.flac'
        Input = @('-f', 'lavfi', '-i', 'sine=frequency=440:duration=0.3', '-ac', '2', '-ar', '96000')
        Args = @('-sample_fmt', 's32') + $meta
    },
    @{
        File = 'sample_multi.flac'
        Input = @('-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=5.1:d=0.3')
        Args = @('-sample_fmt', 's32') + $meta
    }
)
$needSpecial = $specialJobs | Where-Object { -not (Test-Path (Join-Path $OutDir $_.File)) }
if ($needSpecial) {
    $ffmpeg = Resolve-Ffmpeg
    foreach ($j in $needSpecial) {
        $out = Join-Path $OutDir $j.File
        & $ffmpeg -y -hide_banner -loglevel error @($j.Input) @($j.Args) $out
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $($j.File)" }
    }
}

# --- hand-crafted DSF fixture ---------------------------------------------------------------
$dsfPath = Join-Path $OutDir 'sample.dsf'
if (-not (Test-Path $dsfPath)) {
    $channels      = 2
    $blockPerChan  = 4096
    $samplingFreq  = 2822400      # DSD64
    $bitsPerSample = 1
    $dataPayload   = $channels * $blockPerChan         # one block per channel
    $sampleCount   = [uint64]($blockPerChan * 8)       # bits per channel

    $dsdChunkSize  = [uint64]28
    $fmtChunkSize  = [uint64]52
    $dataChunkSize = [uint64](12 + $dataPayload)       # 'data' + size(8) + payload
    $totalSize     = [uint64](28 + 52 + 12 + $dataPayload)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $ascii = [System.Text.Encoding]::ASCII

    # DSD chunk
    $bw.Write($ascii.GetBytes('DSD '))
    $bw.Write($dsdChunkSize)
    $bw.Write($totalSize)
    $bw.Write([uint64]0)                # metadata pointer = 0 (no tag yet)

    # fmt chunk
    $bw.Write($ascii.GetBytes('fmt '))
    $bw.Write($fmtChunkSize)
    $bw.Write([uint32]1)                # format version
    $bw.Write([uint32]0)                # format id (DSD raw)
    $bw.Write([uint32]2)                # channel type (stereo)
    $bw.Write([uint32]$channels)        # channel num
    $bw.Write([uint32]$samplingFreq)
    $bw.Write([uint32]$bitsPerSample)
    $bw.Write([uint64]$sampleCount)
    $bw.Write([uint32]$blockPerChan)
    $bw.Write([uint32]0)                # reserved

    # data chunk (0x69 is DSD silence)
    $bw.Write($ascii.GetBytes('data'))
    $bw.Write($dataChunkSize)
    $bw.Write((,[byte]0x69 * $dataPayload))

    $bw.Flush()
    [System.IO.File]::WriteAllBytes($dsfPath, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
}

Write-Host "Test fixtures ready in $OutDir"
