param(
    [string]$DotnetPath = "dotnet",
    [string]$IsccPath = "",
    [string]$SbomToolPath = "",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$sbomToolVersion = "4.1.5"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$stageRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "stage"))
$appStage = Join-Path $stageRoot "App"
$sbomToolRoot = Join-Path $stageRoot "SbomTool"
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "artifacts"))

function Assert-File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $stream = [System.IO.File]::OpenRead($Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $algorithm.ComputeHash($stream)
        return ([System.BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Reset-GeneratedDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedName
    )

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $parent = [System.IO.Directory]::GetParent($resolved)
    if ($null -eq $parent -or
        $parent.FullName -ne [System.IO.Path]::GetFullPath($PSScriptRoot) -or
        [System.IO.Path]::GetFileName($resolved) -cne $ExpectedName) {
        throw "Refusing to clear an unexpected build directory: $resolved"
    }

    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
}

function Resolve-Iscc {
    if ($IsccPath) {
        Assert-File -Path $IsccPath -Description "Inno Setup compiler"
        return [System.IO.Path]::GetFullPath($IsccPath)
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "Inno Setup was not found. Install the pinned CI version or pass -IsccPath."
}

function Resolve-SbomTool {
    if ($SbomToolPath) {
        Assert-File -Path $SbomToolPath -Description "SBOM generator"
        return [System.IO.Path]::GetFullPath($SbomToolPath)
    }

    New-Item -ItemType Directory -Path $sbomToolRoot -Force | Out-Null
    $installOutput = & $dotnet.Source tool install Microsoft.Sbom.DotNetTool `
        --tool-path $sbomToolRoot `
        --version $sbomToolVersion
    if ($LASTEXITCODE -ne 0) {
        throw "Could not install Microsoft.Sbom.DotNetTool $sbomToolVersion."
    }
    $installOutput | ForEach-Object { Write-Host $_ }

    $tool = Join-Path $sbomToolRoot "sbom-tool.exe"
    Assert-File -Path $tool -Description "Pinned SBOM generator"
    return $tool
}

$dotnet = Get-Command $DotnetPath -ErrorAction Stop
$iscc = Resolve-Iscc

[xml]$buildProperties = Get-Content -LiteralPath `
    (Join-Path $repoRoot "Directory.Build.props") -Raw
$version = [string]$buildProperties.Project.PropertyGroup.VersionPrefix
if (-not $version -or $version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Directory.Build.props does not define a valid VersionPrefix."
}

Reset-GeneratedDirectory -Path $stageRoot -ExpectedName "stage"
Reset-GeneratedDirectory -Path $artifactsRoot -ExpectedName "artifacts"
New-Item -ItemType Directory -Path $appStage -Force | Out-Null

if (-not $NoRestore) {
    & $dotnet.Source restore (Join-Path $repoRoot "Speak.sln") --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "Package restore failed."
    }
}

& $dotnet.Source publish (Join-Path $repoRoot "Speak.csproj") `
    -c Release `
    --no-restore `
    --runtime win-x64 `
    --self-contained true `
    -o $appStage `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Speak publish failed."
}

# A release receives the audited portable configuration. The ignored developer
# appsettings.json is explicitly excluded by Speak.csproj.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "appsettings.portable.json") `
    -Destination (Join-Path $appStage "appsettings.json") -Force

& (Join-Path $PSScriptRoot "verify-publish.ps1") `
    -PublishRoot $appStage `
    -AllowPortableAppSettings

$sbomTool = Resolve-SbomTool
$componentRoot = Join-Path $repoRoot "obj"
Assert-File -Path (Join-Path $componentRoot "project.assets.json") `
    -Description "Restored project component inventory"
$commit = (& git -C $repoRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $commit) {
    throw "A Git commit is required to create the release SBOM namespace."
}
$commit = ([string]$commit).Trim()

& $sbomTool generate `
    -b $appStage `
    -bc $componentRoot `
    -pn Speak `
    -pv $version `
    -ps "Speak contributors" `
    -nsb "https://github.com/absentmindz/Speak/sbom/" `
    -nsu $commit `
    -V Information
if ($LASTEXITCODE -ne 0) {
    throw "SPDX SBOM generation failed."
}

$generatedSbom = Join-Path $appStage "_manifest\spdx_2.2\manifest.spdx.json"
Assert-File -Path $generatedSbom -Description "Generated SPDX 2.2 SBOM"
$releaseSbom = Join-Path $artifactsRoot "Speak-$version.spdx.json"
Copy-Item -LiteralPath $generatedSbom -Destination $releaseSbom

$portableZip = Join-Path $artifactsRoot "Speak-$version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $appStage "*") `
    -DestinationPath $portableZip `
    -CompressionLevel Optimal

& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot "Speak.iss")
if ($LASTEXITCODE -ne 0) {
    throw "Speak installer compilation failed."
}

$expectedInstaller = Join-Path $artifactsRoot "Speak-$version-Setup.exe"
Assert-File -Path $expectedInstaller -Description "Speak installer"
$installerOutputs = @(Get-ChildItem -LiteralPath $artifactsRoot `
    -Filter "Speak-*-Setup.exe" -File)
if ($installerOutputs.Count -ne 1 -or
    $installerOutputs[0].Name -cne "Speak-$version-Setup.exe") {
    throw "Packaging must produce exactly one versioned Speak installer."
}

# Generate checksums only after every release asset exists.
$releaseAssets = @(
    $expectedInstaller,
    $portableZip,
    $releaseSbom
)
$checksumLines = foreach ($asset in $releaseAssets) {
    Assert-File -Path $asset -Description "Release asset"
    $item = Get-Item -LiteralPath $asset
    $hash = Get-Sha256 -Path $item.FullName
    "$hash  $($item.Name)"
}
$checksumLines | Set-Content `
    -LiteralPath (Join-Path $artifactsRoot "SHA256SUMS.txt") `
    -Encoding ascii

& (Join-Path $PSScriptRoot "verify-release-assets.ps1") `
    -ArtifactsRoot $artifactsRoot `
    -Version $version

Write-Host "Package build completed. Verified release assets: $artifactsRoot"
