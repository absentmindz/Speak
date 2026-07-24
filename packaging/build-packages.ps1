param(
    [string]$DotnetPath = "D:\Speak\.dotnet8\dotnet.exe",
    [string]$QwenEnvironment = "D:\Speak\.qwen-tts-env",
    [string]$PythonBase = "C:\Users\hamza\AppData\Roaming\uv\python\cpython-3.11-windows-x86_64-none",
    [string]$WhisperEnvironment = "D:\whisper-gpu-env",
    [string]$ModelsRoot = "D:\Models",
    [string]$IsccPath = "",
    [switch]$Resume,
    [switch]$SkipModelPack
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$stageRoot = Join-Path $PSScriptRoot "stage"
$appStage = Join-Path $stageRoot "App"
$prerequisiteStage = Join-Path $stageRoot "Prerequisites"
$licenseStage = Join-Path $stageRoot "ModelLicenses"
$artifactsRoot = Join-Path $PSScriptRoot "artifacts"

function Assert-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }
}

function Assert-Directory([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description is missing: $Path"
    }
}

function Copy-Tree([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    & robocopy.exe $Source $Destination /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /NFL /NDL /NJH /NJS /NP /XD __pycache__ /XF *.pyc
    if ($LASTEXITCODE -gt 7) {
        throw "Robocopy failed with exit code $LASTEXITCODE while copying $Source"
    }
}

function Resolve-Iscc {
    if ($IsccPath) {
        return $IsccPath
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
        "C:\Program Files\Inno Setup 7\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    throw "Inno Setup compiler was not found."
}

if ((Test-Path -LiteralPath $stageRoot) -and -not $Resume) {
    throw "Packaging stage already exists. Move or archive it before starting a new package build: $stageRoot"
}
if (-not (Test-Path -LiteralPath $stageRoot) -and $Resume) {
    throw "Packaging stage does not exist, so the build cannot resume: $stageRoot"
}

Assert-File $DotnetPath ".NET SDK"
Assert-Directory $QwenEnvironment "Qwen environment"
Assert-Directory $PythonBase "Portable Python base"
Assert-Directory $WhisperEnvironment "Whisper environment"
Assert-Directory $ModelsRoot "Models root"

$runtimeRoot = Join-Path $appStage "Runtime\python"
$runtimePython = Join-Path $runtimeRoot "python.exe"

New-Item -ItemType Directory -Path $appStage, $prerequisiteStage, $licenseStage, $artifactsRoot -Force | Out-Null
if (-not $Resume) {
    & $DotnetPath publish (Join-Path $repoRoot "Speak.csproj") -c Release --no-restore -o $appStage -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "Speak publish failed."
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "appsettings.portable.json") -Destination (Join-Path $appStage "appsettings.json") -Force
    Copy-Tree $PythonBase $runtimeRoot
    Copy-Tree (Join-Path $QwenEnvironment "Lib\site-packages") (Join-Path $runtimeRoot "Lib\site-packages")
}

Assert-File $runtimePython "Staged Python runtime"
& $runtimePython -m pip install --disable-pip-version-check --break-system-packages --no-deps --upgrade openai-whisper==20250625 tiktoken==0.13.0 more-itertools==11.1.0
if ($LASTEXITCODE -ne 0) {
    throw "Could not add Whisper packages to the shared runtime."
}

$ffmpegTarget = Join-Path $appStage "Tools\ffmpeg\bin"
New-Item -ItemType Directory -Path $ffmpegTarget -Force | Out-Null
$ffmpegNames = @("ffmpeg.exe", "avcodec-62.dll", "avdevice-62.dll", "avfilter-11.dll", "avformat-62.dll", "avutil-60.dll", "swresample-6.dll", "swscale-9.dll")
foreach ($name in $ffmpegNames) {
    Copy-Item -LiteralPath (Join-Path $WhisperEnvironment "Scripts\$name") -Destination $ffmpegTarget
}

& $runtimePython -c "import json,sys,torch,whisper,qwen_tts,soundfile; print(json.dumps({'python':sys.version.split()[0],'torch':torch.__version__,'cuda':torch.version.cuda,'whisper':getattr(whisper,'__version__','unknown')}))"
if ($LASTEXITCODE -ne 0) {
    throw "Portable Python runtime import test failed."
}

& $runtimePython -m pip --disable-pip-version-check freeze | Set-Content -LiteralPath (Join-Path $appStage "Runtime\requirements-lock.txt") -Encoding utf8

$vcRedist = Join-Path $PSScriptRoot "downloads\vc_redist.x64.exe"
Assert-File $vcRedist "Microsoft Visual C++ Redistributable"
Copy-Item -LiteralPath $vcRedist -Destination (Join-Path $prerequisiteStage "vc_redist.x64.exe")

Copy-Item -LiteralPath (Join-Path $QwenEnvironment "Lib\site-packages\qwen_tts-0.1.1.dist-info\licenses\LICENSE") -Destination (Join-Path $licenseStage "Qwen3-TTS-Apache-2.0-LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $WhisperEnvironment "Lib\site-packages\openai_whisper-20250625.dist-info\licenses\LICENSE") -Destination (Join-Path $licenseStage "Whisper-MIT-LICENSE.txt")

$appLicenses = Join-Path $appStage "Licenses"
New-Item -ItemType Directory -Path $appLicenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "licenses\FFmpeg-GPL-3.0.txt") -Destination $appLicenses
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "FFmpeg-NOTICE.txt") -Destination $appLicenses

$modelFiles = @((Join-Path $ModelsRoot "whisper\large-v3.pt"))
$modelFiles += @(Get-ChildItem -LiteralPath (Join-Path $ModelsRoot "Qwen3-TTS-12Hz-1.7B-CustomVoice") -Recurse -File | Select-Object -ExpandProperty FullName)
$modelFiles += @(Get-ChildItem -LiteralPath (Join-Path $ModelsRoot "Qwen3-TTS-12Hz-1.7B-Base") -Recurse -File | Select-Object -ExpandProperty FullName)

$manifestLines = foreach ($file in $modelFiles) {
    $relative = [System.IO.Path]::GetRelativePath($ModelsRoot, $file).Replace('\', '/')
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
    "$hash  $relative"
}
$manifestLines | Set-Content -LiteralPath (Join-Path $stageRoot "models-manifest.sha256") -Encoding ascii

$iscc = Resolve-Iscc
& $iscc (Join-Path $PSScriptRoot "Speak.iss")
if ($LASTEXITCODE -ne 0) {
    throw "Speak installer compilation failed."
}

if (-not $SkipModelPack) {
    & $iscc (Join-Path $PSScriptRoot "SpeakModels.iss")
    if ($LASTEXITCODE -ne 0) {
        throw "Offline model-pack compilation failed."
    }
}

Write-Host "Package build completed."
