[CmdletBinding()]
param(
    [string]$OutputPath = "docs\demo\speak-demo.mp4",
    [string]$PythonPath = "python",
    [string]$FfmpegPath,
    [string]$MusicPath = "marketing\audio\speak-demo-original-bed.m4a",
    [switch]$NoMusic,
    [switch]$RegenerateMusic
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

if ($NoMusic -and $RegenerateMusic) {
    throw '-NoMusic and -RegenerateMusic cannot be used together.'
}

if ([string]::IsNullOrWhiteSpace($FfmpegPath)) {
    $ffmpeg = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($null -ne $ffmpeg) {
        $FfmpegPath = $ffmpeg.Source
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:SPEAK_FFMPEG_BIN)) {
        $candidate = Join-Path $env:SPEAK_FFMPEG_BIN 'ffmpeg.exe'
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "SPEAK_FFMPEG_BIN does not contain ffmpeg.exe: $candidate"
        }
        $FfmpegPath = $candidate
    }
    else {
        throw 'FFmpeg was not found. Add it to PATH, set SPEAK_FFMPEG_BIN, or pass -FfmpegPath.'
    }
}

function Resolve-RepositoryPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

$scriptPath = Join-Path $PSScriptRoot 'render_demo.py'
$resolvedOutput = Resolve-RepositoryPath $OutputPath
$rendererArguments = @($scriptPath, '--ffmpeg', $FfmpegPath, '--output', $resolvedOutput)

if (-not $NoMusic) {
    $resolvedMusic = Resolve-RepositoryPath $MusicPath
    if ($RegenerateMusic) {
        $musicGenerator = Join-Path $PSScriptRoot 'generate_demo_music.py'
        & $PythonPath $musicGenerator --ffmpeg $FfmpegPath --output $resolvedMusic
        if ($LASTEXITCODE -ne 0) {
            throw "Demo music generator failed with exit code $LASTEXITCODE."
        }
    }

    if (-not (Test-Path -LiteralPath $resolvedMusic -PathType Leaf)) {
        throw "Demo music was not found: $resolvedMusic. Run again with -RegenerateMusic or use -NoMusic."
    }

    $rendererArguments += @('--music', $resolvedMusic)
}

& $PythonPath @rendererArguments
if ($LASTEXITCODE -ne 0) {
    throw "Demo renderer failed with exit code $LASTEXITCODE."
}

Write-Host "Rendered Speak demo: $resolvedOutput"
