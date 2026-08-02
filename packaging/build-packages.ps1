param(
    [string]$DotnetPath = "dotnet",
    [string]$BuildPythonPath = "python",
    [string]$IsccPath = "",
    [string]$SbomToolPath = "",
    [string]$SourceCacheRoot = "",
    [string]$PythonArchivePath = "",
    [string]$PythonSigstoreBundlePath = "",
    [string]$SigstorePath = "",
    [string]$EdgeTtsWheelCache = "",
    [string]$EdgeTtsWheelManifestPath = "",
    [switch]$SkipCompleteInstaller,
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
$completeRoot = Join-Path $PSScriptRoot "complete"
$sourceLockPath = Join-Path $completeRoot "sources.lock.json"
$edgeRequirementsPath = Join-Path $repoRoot "tools\requirements-edge-tts.lock.txt"
$trackedEdgeWheelManifestPath = Join-Path $PSScriptRoot "edge-tts-wheelhouse.manifest.json"

. (Join-Path $completeRoot "CompleteInstaller.Common.ps1")
. (Join-Path $PSScriptRoot "EdgeTtsRuntime.Common.ps1")

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

function Resolve-StandardCachePath {
    param(
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$DefaultLeaf,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $resolved = if ([string]::IsNullOrWhiteSpace($Value)) {
        Join-Path $SourceCacheRoot $DefaultLeaf
    }
    else {
        $Value
    }
    Assert-CompleteFile -Path $resolved -Description $Description | Out-Null
    return [System.IO.Path]::GetFullPath($resolved)
}

function Add-EdgeTtsPackagesToSbom {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SbomPath,
        [Parameter(Mandatory = $true)]
        [object]$SourceLock,
        [Parameter(Mandatory = $true)]
        [object]$WheelManifest
    )

    $sbom = Read-CompleteJson -Path $SbomPath -Description "Generated SPDX 2.2 SBOM"
    $packages = [System.Collections.Generic.List[object]]::new()
    foreach ($package in @($sbom.packages)) {
        $packages.Add($package)
    }
    $relationships = [System.Collections.Generic.List[object]]::new()
    foreach ($relationship in @($sbom.relationships)) {
        $relationships.Add($relationship)
    }

    $pythonSpdxId = 'SPDXRef-Package-EdgeRuntime-CPython'
    $packages.Add([ordered]@{
        name = 'CPython'
        SPDXID = $pythonSpdxId
        versionInfo = [string]$SourceLock.python.version
        downloadLocation = [string]$SourceLock.python.sourceUrl
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'PSF-2.0'
        copyrightText = 'NOASSERTION'
        checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = [string]$SourceLock.python.sha256 })
        supplier = 'Organization: Python Software Foundation'
        comment = 'Portable Python runtime used only to launch the bundled edge-tts module.'
    })
    $relationships.Add([ordered]@{
        spdxElementId = 'SPDXRef-RootPackage'
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $pythonSpdxId
    })

    foreach ($wheel in @($WheelManifest.files)) {
        $normalizedName = ([string]$wheel.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        $normalizedVersion = [regex]::Replace(([string]$wheel.version), '[^A-Za-z0-9.-]', '-')
        $spdxId = 'SPDXRef-Package-EdgeRuntime-' + [regex]::Replace($normalizedName, '[^A-Za-z0-9.-]', '-') + '-' + $normalizedVersion
        $packages.Add([ordered]@{
            name = [string]$wheel.distribution
            SPDXID = $spdxId
            versionInfo = [string]$wheel.version
            downloadLocation = [string]$wheel.sourceUrl
            filesAnalyzed = $false
            licenseConcluded = 'NOASSERTION'
            licenseDeclared = [string]$wheel.licenseDeclared
            copyrightText = 'NOASSERTION'
            checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = [string]$wheel.sha256 })
            supplier = 'NOASSERTION'
            comment = 'Checksum identifies the audited cached wheel listed in the Edge TTS wheel manifest.'
        })
        $relationships.Add([ordered]@{
            spdxElementId = 'SPDXRef-RootPackage'
            relationshipType = 'DEPENDS_ON'
            relatedSpdxElement = $spdxId
        })
    }

    $sbom.packages = @($packages)
    $sbom.relationships = @($relationships)
    Write-CompleteJson -Value $sbom -Path $SbomPath
}

$dotnet = Get-Command $DotnetPath -ErrorAction Stop
$iscc = Resolve-Iscc

if ([string]::IsNullOrWhiteSpace($SourceCacheRoot)) {
    throw "Bundled Edge TTS packaging requires the audited source cache. Pass -SourceCacheRoot."
}
$sourceCache = Assert-CompleteDirectory -Path $SourceCacheRoot -Description "Speak packaging source cache"
$sourceLock = Read-CompleteJson -Path $sourceLockPath -Description "Complete-installer source lock"
if ([int]$sourceLock.schemaVersion -ne 1 -or
    [string]$sourceLock.python.version -cne '3.14.6' -or
    [string]$sourceLock.python.license.relativePath -cne 'LICENSE.txt' -or
    [string]$sourceLock.edgeTts.version -cne '7.2.8') {
    throw "The source lock does not define the required Python 3.14.6 baseline."
}
$buildPython = Get-Command $BuildPythonPath -ErrorAction Stop
$buildPythonVersion = @(& $buildPython.Source -c "import sys; print('.'.join(map(str, sys.version_info[:3])))" 2>&1)
if ($LASTEXITCODE -ne 0 -or (($buildPythonVersion -join "`n").Trim() -cne '3.14.6')) {
    throw "Bundled Edge TTS staging requires build Python 3.14.6."
}
$pythonArchive = Resolve-StandardCachePath -Value $PythonArchivePath -DefaultLeaf ([string]$sourceLock.python.fileName) -Description "Python 3.14.6 archive"
$pythonBundle = Resolve-StandardCachePath -Value $PythonSigstoreBundlePath -DefaultLeaf ([string]$sourceLock.python.sigstore.fileName) -Description "Python 3.14.6 Sigstore bundle"
if ([string]::IsNullOrWhiteSpace($SigstorePath)) {
    throw "A sigstore verifier is required to validate the bundled Python source. Pass -SigstorePath."
}
$sigstore = (Assert-CompleteFile -Path $SigstorePath -Description "sigstore verifier").FullName
Assert-CompleteExactFile -Path $pythonArchive -Description "Python 3.14.6 archive" `
    -ExpectedSizeBytes ([Int64]$sourceLock.python.sizeBytes) -ExpectedSha256 ([string]$sourceLock.python.sha256) | Out-Null
Assert-CompleteExactFile -Path $pythonBundle -Description "Python 3.14.6 Sigstore bundle" `
    -ExpectedSizeBytes ([Int64]$sourceLock.python.sigstore.sizeBytes) -ExpectedSha256 ([string]$sourceLock.python.sigstore.sha256) | Out-Null
Invoke-CompleteSigstoreVerification -SigstorePath $sigstore -ArtifactPath $pythonArchive -BundlePath $pythonBundle `
    -Identity ([string]$sourceLock.python.sigstore.identity) -OidcIssuer ([string]$sourceLock.python.sigstore.oidcIssuer)
$edgeWheelRoot = if ([string]::IsNullOrWhiteSpace($EdgeTtsWheelCache)) { Join-Path $sourceCache.FullName "edge-tts-wheelhouse" } else { $EdgeTtsWheelCache }
$edgeWheelManifestPath = if ([string]::IsNullOrWhiteSpace($EdgeTtsWheelManifestPath)) { Join-Path $sourceCache.FullName "edge-tts-wheelhouse.manifest.json" } else { $EdgeTtsWheelManifestPath }
$edgeWheelManifestPath = (Assert-CompleteFile -Path $edgeWheelManifestPath -Description "Cached Edge TTS wheel manifest").FullName
if ((Get-CompleteSha256 -Path $edgeWheelManifestPath) -cne (Get-CompleteSha256 -Path $trackedEdgeWheelManifestPath)) {
    throw "The cached Edge TTS wheel manifest differs from the tracked audited manifest."
}
$edgePinnedRequirements = Get-EdgeTtsPinnedRequirements -Path $edgeRequirementsPath
$edgeWheelManifest = Assert-EdgeTtsWheelhouse -WheelRoot $edgeWheelRoot -ManifestPath $edgeWheelManifestPath `
    -RequirementsPath $edgeRequirementsPath -PinnedRequirements $edgePinnedRequirements

[xml]$buildProperties = Get-Content -LiteralPath `
    (Join-Path $repoRoot "Directory.Build.props") -Raw
$version = [string]$buildProperties.Project.PropertyGroup.VersionPrefix
if (-not $version -or $version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Directory.Build.props does not define a valid VersionPrefix."
}

New-Item -ItemType Directory -Path $stageRoot, $artifactsRoot -Force | Out-Null
Assert-CompleteNoReparseAncestors -Path $stageRoot -Description "Standard packaging stage root"
Assert-CompleteNoReparseAncestors -Path $artifactsRoot -Description "Standard packaging artifact root"
$appStage = Reset-CompleteGeneratedDirectory -Path $appStage -ExpectedParent $stageRoot -ExpectedLeaf "App"
$sbomToolRoot = Reset-CompleteGeneratedDirectory -Path $sbomToolRoot -ExpectedParent $stageRoot -ExpectedLeaf "SbomTool"
foreach ($file in @(Get-ChildItem -LiteralPath $artifactsRoot -File -Force | Where-Object {
    $_.Name -match '^Speak-.*-Setup\.exe$' -or
    $_.Name -match '^Speak-.*-Complete-Offline-Setup\.exe$' -or
    $_.Name -match '^Speak-.*-win-x64-portable\.zip$' -or
    $_.Name -match '^Speak-.*\.spdx\.json$' -or
    $_.Name -ceq 'SHA256SUMS.txt'
})) {
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to remove a reparse-point release artifact: $($file.FullName)"
    }
    Remove-Item -LiteralPath $file.FullName -Force
}
$completeArtifactDirectory = Join-Path $artifactsRoot 'complete'
if (Test-Path -LiteralPath $completeArtifactDirectory) {
    Assert-CompleteTreeNoReparsePoints -RootPath $completeArtifactDirectory -Description "Complete installer intermediate artifact directory"
    Remove-Item -LiteralPath $completeArtifactDirectory -Recurse -Force
}

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

$edgeRuntimeRoot = Join-Path $appStage "Runtime\EdgeTts\7.2.8"
$edgeRuntimePython = [string](Initialize-EdgeTtsRuntime -PythonArchive $pythonArchive -RuntimeRoot $edgeRuntimeRoot `
    -WheelRoot $edgeWheelRoot -RequirementsPath $edgeRequirementsPath -BuildPython $buildPython.Source `
    -PinnedRequirements $edgePinnedRequirements)
Assert-CompleteFile -Path $edgeRuntimePython -Description "Packaged Edge TTS Python runtime" | Out-Null
Assert-EdgeTtsRuntimeLicenseInventory -RuntimeRoot $edgeRuntimeRoot `
    -PinnedRequirements $edgePinnedRequirements -WheelManifest $edgeWheelManifest `
    -PythonSourceLock $sourceLock.python
$edgeRuntimeSupport = Join-Path $edgeRuntimeRoot 'Support'
New-Item -ItemType Directory -Path $edgeRuntimeSupport -Force | Out-Null
$stagedEdgeRequirements = Join-Path $edgeRuntimeSupport 'requirements-edge-tts.lock.txt'
$stagedEdgeManifest = Join-Path $edgeRuntimeSupport 'edge-tts-wheelhouse.manifest.json'
$stagedEdgeInventory = Join-Path $edgeRuntimeSupport 'edge-tts-runtime.inventory.json'
Copy-Item -LiteralPath $edgeRequirementsPath -Destination $stagedEdgeRequirements -Force
Copy-Item -LiteralPath $edgeWheelManifestPath -Destination $stagedEdgeManifest -Force
Write-EdgeTtsRuntimeInventory -RuntimeRoot $edgeRuntimeRoot -RequirementsPath $stagedEdgeRequirements `
    -WheelManifestPath $stagedEdgeManifest -PinnedRequirements $edgePinnedRequirements `
    -WheelManifest $edgeWheelManifest -PythonSourceLock $sourceLock.python -InventoryPath $stagedEdgeInventory

& (Join-Path $PSScriptRoot "verify-publish.ps1") `
    -PublishRoot $appStage `
    -AllowPortableAppSettings `
    -RequireBundledEdgeTts

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
Add-EdgeTtsPackagesToSbom -SbomPath $generatedSbom -SourceLock $sourceLock -WheelManifest $edgeWheelManifest
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

$completeInstaller = Join-Path $artifactsRoot "Speak-$version-Complete-Offline-Setup.exe"
if (-not $SkipCompleteInstaller) {
    & (Join-Path $PSScriptRoot 'build-complete-installer.ps1') `
        -DotnetPath $dotnet.Source `
        -BuildPythonPath $buildPython.Source `
        -IsccPath $iscc `
        -SourceCacheRoot $sourceCache.FullName `
        -SigstorePath $sigstore `
        -NoRestore
    if ($LASTEXITCODE -ne 0) {
        throw "Complete offline installer build failed."
    }
    $builtCompleteInstaller = Join-Path $completeArtifactDirectory "Speak-$version-Complete-Offline-Setup.exe"
    Assert-CompleteFile -Path $builtCompleteInstaller -Description "Built Complete offline installer" | Out-Null
    Copy-Item -LiteralPath $builtCompleteInstaller -Destination $completeInstaller -Force
    Assert-CompleteTreeNoReparsePoints -RootPath $completeArtifactDirectory -Description "Complete installer intermediate artifact directory"
    Remove-Item -LiteralPath $completeArtifactDirectory -Recurse -Force
}

# Generate checksums only after every release asset exists.
$releaseAssets = @(
    $expectedInstaller,
    $portableZip,
    $releaseSbom
)
if (-not $SkipCompleteInstaller) {
    $releaseAssets += $completeInstaller
}
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
    -Version $version `
    -AllowMissingCompleteInstaller:$SkipCompleteInstaller

Write-Host "Package build completed. Verified release assets: $artifactsRoot"
