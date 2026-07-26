param(
    [string]$DotnetPath = "dotnet",
    [string]$ModelsRoot = "",
    [string]$IsccPath = "",
    [switch]$SkipInstaller,
    [switch]$SkipModelPack
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not $SkipInstaller -and -not $SkipModelPack) {
    throw "Offline model-pack production is disabled until an audited provenance manifest is checked in. Use -SkipModelPack."
}

function Get-SpeakRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,
        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $targetFullPath = [System.IO.Path]::GetFullPath($TargetPath)
    $baseUri = [Uri]::new($baseFullPath)
    $targetUri = [Uri]::new($targetFullPath)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace(
        [System.IO.Path]::AltDirectorySeparatorChar,
        [System.IO.Path]::DirectorySeparatorChar)
}

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$stageRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "stage"))
$appStage = Join-Path $stageRoot "App"
$modelLicenseStage = Join-Path $stageRoot "ModelLicenses"
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "artifacts"))

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

function Reset-GeneratedDirectory([string]$Path, [string]$ExpectedName) {
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $parent = [System.IO.Directory]::GetParent($resolved)
    if ($null -eq $parent -or
        $parent.FullName -ne [System.IO.Path]::GetFullPath($PSScriptRoot) -or
        [System.IO.Path]::GetFileName($resolved) -ne $ExpectedName) {
        throw "Refusing to clear an unexpected build directory: $resolved"
    }

    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
}

function Resolve-Iscc {
    if ($IsccPath) {
        Assert-File $IsccPath "Inno Setup compiler"
        return [System.IO.Path]::GetFullPath($IsccPath)
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "Inno Setup was not found. Pass -IsccPath or use -SkipInstaller."
}

$dotnet = Get-Command $DotnetPath -ErrorAction Stop
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw
$version = [string]$buildProperties.Project.PropertyGroup.VersionPrefix
if (-not $version) {
    throw "Directory.Build.props does not define VersionPrefix."
}

Reset-GeneratedDirectory $stageRoot "stage"
Reset-GeneratedDirectory $artifactsRoot "artifacts"
New-Item -ItemType Directory -Path $appStage, $modelLicenseStage -Force | Out-Null

& $dotnet.Source restore (Join-Path $repoRoot "Speak.sln") --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "Package restore failed."
}

& $dotnet.Source publish (Join-Path $repoRoot "Speak.csproj") `
    -c Release `
    --no-restore `
    -o $appStage `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Speak publish failed."
}

# A published build receives the audited portable configuration. The ignored
# developer appsettings.json is explicitly excluded by Speak.csproj.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "appsettings.portable.json") `
    -Destination (Join-Path $appStage "appsettings.json") -Force

& (Join-Path $PSScriptRoot "verify-publish.ps1") `
    -PublishRoot $appStage `
    -AllowPortableAppSettings

$portableZip = Join-Path $artifactsRoot "Speak-$version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $appStage "*") -DestinationPath $portableZip `
    -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $iscc = Resolve-Iscc
    & $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot "Speak.iss")
    if ($LASTEXITCODE -ne 0) {
        throw "Speak installer compilation failed."
    }

    if (-not $SkipModelPack) {
        if (-not $ModelsRoot) {
            throw "Pass -ModelsRoot to build the optional model pack, or use -SkipModelPack."
        }

        $ModelsRoot = [System.IO.Path]::GetFullPath($ModelsRoot)
        Assert-Directory $ModelsRoot "Models root"
        Assert-File (Join-Path $ModelsRoot "whisper\large-v3.pt") "Whisper large-v3 model"
        Assert-Directory (Join-Path $ModelsRoot "Qwen3-TTS-12Hz-1.7B-CustomVoice") "Qwen3 CustomVoice model"
        Assert-Directory (Join-Path $ModelsRoot "Qwen3-TTS-12Hz-1.7B-Base") "Qwen3 Base model"

        Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") `
            -Destination (Join-Path $modelLicenseStage "Qwen3-TTS-Apache-2.0-LICENSE.txt")
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "model-licenses\Whisper-MIT-LICENSE.txt") `
            -Destination $modelLicenseStage

        $modelFiles = @((Join-Path $ModelsRoot "whisper\large-v3.pt"))
        $modelFiles += @(Get-ChildItem -LiteralPath (Join-Path $ModelsRoot "Qwen3-TTS-12Hz-1.7B-CustomVoice") -Recurse -File |
            Select-Object -ExpandProperty FullName)
        $modelFiles += @(Get-ChildItem -LiteralPath (Join-Path $ModelsRoot "Qwen3-TTS-12Hz-1.7B-Base") -Recurse -File |
            Select-Object -ExpandProperty FullName)

        $modelManifest = foreach ($file in $modelFiles) {
            $relative = (Get-SpeakRelativePath -BasePath $ModelsRoot -TargetPath $file).Replace('\', '/')
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
        $modelManifest | Set-Content -LiteralPath (Join-Path $stageRoot "models-manifest.sha256") -Encoding ascii

        & $iscc "/DAppVersion=$version" "/DModelsRoot=$ModelsRoot" `
            (Join-Path $PSScriptRoot "SpeakModels.iss")
        if ($LASTEXITCODE -ne 0) {
            throw "Offline model-pack compilation failed."
        }
    }
}

$dependencyInventory = Join-Path $artifactsRoot "Speak-$version-dependencies.json"
& $dotnet.Source list (Join-Path $repoRoot "Speak.csproj") package `
    --include-transitive --format json |
    Set-Content -LiteralPath $dependencyInventory -Encoding utf8
if ($LASTEXITCODE -ne 0) {
    throw "Could not create the dependency inventory."
}

$commit = (& git -C $repoRoot rev-parse HEAD 2>$null)
$buildInfo = [ordered]@{
    version = $version
    commit = if ($LASTEXITCODE -eq 0) { $commit } else { "unknown" }
    builtAtUtc = [DateTime]::UtcNow.ToString("O")
    dotnetSdk = (& $dotnet.Source --version)
    runtime = "win-x64"
}
$buildInfo | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $artifactsRoot "BUILD-INFO.json") -Encoding utf8

$checksumFile = Join-Path $artifactsRoot "SHA256SUMS.txt"
$checksumLines = foreach ($file in Get-ChildItem -LiteralPath $artifactsRoot -Recurse -File |
    Where-Object { $_.FullName -ne $checksumFile } |
    Sort-Object FullName) {
    $relative = (Get-SpeakRelativePath -BasePath $artifactsRoot -TargetPath $file.FullName).Replace('\', '/')
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    "$hash  $relative"
}
$checksumLines | Set-Content -LiteralPath $checksumFile -Encoding ascii

Write-Host "Package build completed. Artifacts: $artifactsRoot"
