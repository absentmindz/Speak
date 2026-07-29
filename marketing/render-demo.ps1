[CmdletBinding()]
param(
    [string]$OutputPath = "docs\demo\speak-demo.mp4",
    [string]$PythonPath = "python",
    [string]$FfmpegPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

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

$scriptPath = Join-Path $PSScriptRoot 'render_demo.py'
$resolvedOutput = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
& $PythonPath $scriptPath --ffmpeg $FfmpegPath --output $resolvedOutput
if ($LASTEXITCODE -ne 0) {
    throw "Demo renderer failed with exit code $LASTEXITCODE."
}

Write-Host "Rendered Speak demo: $resolvedOutput"
