param(
    [string]$ArtifactsRoot = (Join-Path $PSScriptRoot "artifacts"),
    [string]$Version = "",
    [switch]$AllowMissingCompleteInstaller
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = [System.IO.Path]::GetFullPath($ArtifactsRoot)
. (Join-Path $PSScriptRoot "complete\CompleteInstaller.Common.ps1")
. (Join-Path $PSScriptRoot "EdgeTtsRuntime.Common.ps1")
$sourceLockPath = Join-Path $PSScriptRoot "complete\sources.lock.json"
$edgeRequirementsPath = Join-Path $repoRoot "tools\requirements-edge-tts.lock.txt"
$edgeManifestPath = Join-Path $PSScriptRoot "edge-tts-wheelhouse.manifest.json"

if (-not $Version) {
    [xml]$buildProperties = Get-Content -LiteralPath `
        (Join-Path $repoRoot "Directory.Build.props") -Raw
    $Version = [string]$buildProperties.Project.PropertyGroup.VersionPrefix
}

if (-not $Version -or $Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "A valid release version is required. Received: '$Version'."
}

$sourceLock = Read-CompleteJson -Path $sourceLockPath -Description "Complete-installer source lock"

if (-not (Test-Path -LiteralPath $artifacts -PathType Container)) {
    throw "Release artifact directory does not exist: $artifacts"
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

$expectedNames = @(
    "Speak-$Version-Setup.exe",
    "Speak-$Version-win-x64-portable.zip",
    "Speak-$Version.spdx.json"
)
$completeInstallerName = "Speak-$Version-Complete-Offline-Setup.exe"
if (-not $AllowMissingCompleteInstaller) {
    $expectedNames += $completeInstallerName
}
$expectedNames += "SHA256SUMS.txt"
$expectedAssetNames = @($expectedNames | Where-Object { $_ -cne "SHA256SUMS.txt" })

$actualFiles = @(Get-ChildItem -LiteralPath $artifacts -File)
$actualNames = @($actualFiles | ForEach-Object { $_.Name } | Sort-Object)
$sortedExpectedNames = @($expectedNames | Sort-Object)

if (($actualNames.Count -ne $sortedExpectedNames.Count) -or
    (($actualNames -join "`n") -cne ($sortedExpectedNames -join "`n"))) {
    throw "Release directory must contain exactly these files:`n$($sortedExpectedNames -join [Environment]::NewLine)`nFound:`n$($actualNames -join [Environment]::NewLine)"
}

foreach ($name in $expectedAssetNames) {
    $path = Join-Path $artifacts $name
    if ((Get-Item -LiteralPath $path).Length -le 0) {
        throw "Release asset is empty: $name"
    }
}

$checksumPath = Join-Path $artifacts "SHA256SUMS.txt"
$checksumLines = @(Get-Content -LiteralPath $checksumPath)
if ($checksumLines.Count -ne $expectedAssetNames.Count) {
    throw "SHA256SUMS.txt must contain exactly one entry for each of the $($expectedAssetNames.Count) release assets."
}

$seenNames = @{}
foreach ($line in $checksumLines) {
    if ($line -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<name>[^\\/]+)$') {
        throw "Invalid SHA256SUMS.txt line: '$line'"
    }

    $name = $Matches.name
    $expectedHash = $Matches.hash
    if ($seenNames.ContainsKey($name)) {
        throw "Duplicate checksum entry: $name"
    }
    $seenNames[$name] = $true

    if ($expectedAssetNames -cnotcontains $name) {
        throw "Checksum file contains an unexpected release asset: $name"
    }

    $actualHash = Get-Sha256 -Path (Join-Path $artifacts $name)
    if ($actualHash -cne $expectedHash) {
        throw "SHA-256 verification failed for $name."
    }
}

foreach ($name in $expectedAssetNames) {
    if (-not $seenNames.ContainsKey($name)) {
        throw "Checksum file is missing release asset: $name"
    }
}

$installerPath = Join-Path $artifacts "Speak-$Version-Setup.exe"
$installerVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    $installerPath).ProductVersion
if (([string]$installerVersion).Trim() -cne $Version) {
    throw "Installer product version does not match release version $Version."
}

$installerStream = [System.IO.File]::OpenRead($installerPath)
try {
    if (($installerStream.ReadByte() -ne 0x4D) -or
        ($installerStream.ReadByte() -ne 0x5A)) {
        throw "Installer does not have a valid Windows executable header."
    }
}
finally {
    $installerStream.Dispose()
}

if (-not $AllowMissingCompleteInstaller) {
    $completeInstallerPath = Join-Path $artifacts $completeInstallerName
    $completeVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($completeInstallerPath).ProductVersion
    if (([string]$completeVersion).Trim() -cne $Version) {
        throw "Complete installer product version does not match release version $Version."
    }
    $completeStream = [System.IO.File]::OpenRead($completeInstallerPath)
    try {
        if (($completeStream.ReadByte() -ne 0x4D) -or ($completeStream.ReadByte() -ne 0x5A)) {
            throw "Complete installer does not have a valid Windows executable header."
        }
    }
    finally {
        $completeStream.Dispose()
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $artifacts "Speak-$Version-win-x64-portable.zip"
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entryNames = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $requiredPortableFiles = @(
        "Speak.exe",
        "Speak.deps.json",
        "Speak.runtimeconfig.json",
        "coreclr.dll",
        "hostfxr.dll",
        "hostpolicy.dll",
        "THIRD-PARTY-NOTICES.txt",
        "Runtime/EdgeTts/7.2.8/LICENSE.txt",
        "Runtime/EdgeTts/7.2.8/python.exe",
        "Runtime/EdgeTts/7.2.8/Lib/site-packages/edge_tts/__main__.py",
        "Runtime/EdgeTts/7.2.8/Support/requirements-edge-tts.lock.txt",
        "Runtime/EdgeTts/7.2.8/Support/edge-tts-wheelhouse.manifest.json",
        "Runtime/EdgeTts/7.2.8/Support/edge-tts-runtime.inventory.json",
        "_manifest/spdx_2.2/manifest.spdx.json"
    )
    foreach ($requiredFile in $requiredPortableFiles) {
        if ($entryNames -notcontains $requiredFile) {
            throw "Portable ZIP is not a complete self-contained release; missing $requiredFile."
        }
    }

    $unsafeEntries = @($entryNames | Where-Object {
        $_.StartsWith('/') -or
        $_ -match '^[A-Za-z]:' -or
        $_ -match '(^|/)\.\.(/|$)'
    })
    if ($unsafeEntries.Count -gt 0) {
        throw "Portable ZIP contains unsafe paths: $($unsafeEntries -join ', ')"
    }
}
finally {
    $zip.Dispose()
}

$portableSmokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("Speak-portable-edge-smoke-" + [Guid]::NewGuid().ToString('N'))
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $portableSmokeRoot)
    $edgeRuntimeRoot = Join-Path $portableSmokeRoot "Runtime\EdgeTts\7.2.8"
    $edgePython = Join-Path $edgeRuntimeRoot "python.exe"
    foreach ($complianceFile in @('LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.txt')) {
        $portableCompliance = Join-Path $portableSmokeRoot $complianceFile
        $trackedCompliance = Join-Path $repoRoot $complianceFile
        if ((Get-CompleteSha256 -Path $portableCompliance) -cne (Get-CompleteSha256 -Path $trackedCompliance)) {
            throw "Portable compliance file differs from the tracked source: $complianceFile"
        }
    }
    $edgePinnedRequirements = Get-EdgeTtsPinnedRequirements -Path $edgeRequirementsPath
    $stagedRequirementsPath = Join-Path $edgeRuntimeRoot 'Support\requirements-edge-tts.lock.txt'
    $stagedManifestPath = Join-Path $edgeRuntimeRoot 'Support\edge-tts-wheelhouse.manifest.json'
    $stagedInventoryPath = Join-Path $edgeRuntimeRoot 'Support\edge-tts-runtime.inventory.json'
    if ((Get-CompleteSha256 -Path $stagedRequirementsPath) -cne (Get-CompleteSha256 -Path $edgeRequirementsPath) -or
        (Get-CompleteSha256 -Path $stagedManifestPath) -cne (Get-CompleteSha256 -Path $edgeManifestPath)) {
        throw "Portable Edge TTS lock or wheel manifest differs from the tracked audited evidence."
    }
    $edgeWheelManifest = Read-CompleteJson -Path $stagedManifestPath -Description "Portable Edge TTS wheel manifest"
    if ([string]$edgeWheelManifest.requirementsSha256 -cne (Get-CompleteSha256 -Path $edgeRequirementsPath)) {
        throw "The tracked Edge TTS wheel manifest does not bind to the current requirement lock."
    }
    Assert-EdgeTtsRuntimeLicenseInventory -RuntimeRoot $edgeRuntimeRoot `
        -PinnedRequirements $edgePinnedRequirements -WheelManifest $edgeWheelManifest `
        -PythonSourceLock $sourceLock.python
    Assert-EdgeTtsRuntimeInventory -RuntimeRoot $edgeRuntimeRoot -RequirementsPath $stagedRequirementsPath `
        -WheelManifestPath $stagedManifestPath -PinnedRequirements $edgePinnedRequirements `
        -WheelManifest $edgeWheelManifest -PythonSourceLock $sourceLock.python -InventoryPath $stagedInventoryPath
    foreach ($forbiddenDirectory in @(
        (Join-Path $edgeRuntimeRoot 'Scripts'),
        (Join-Path $edgeRuntimeRoot 'Lib\site-packages\bin'),
        (Join-Path $edgeRuntimeRoot 'Lib\site-packages\pip')
    )) {
        if (Test-Path -LiteralPath $forbiddenDirectory) {
            throw "Portable Edge TTS runtime contains pip or path-bound launchers: $forbiddenDirectory"
        }
    }
    $previousNoByteCode = $env:PYTHONDONTWRITEBYTECODE
    $env:PYTHONDONTWRITEBYTECODE = '1'
    try {
        $edgeVersionOutput = @(& $edgePython -B -I -m edge_tts --version 2>&1)
        $edgeVersionExitCode = $LASTEXITCODE
    }
    finally {
        $env:PYTHONDONTWRITEBYTECODE = $previousNoByteCode
    }
    if ($edgeVersionExitCode -ne 0 -or (($edgeVersionOutput -join "`n").Trim() -cne 'edge-tts 7.2.8')) {
        throw "Relocated portable Edge TTS runtime failed its module launch check: $($edgeVersionOutput -join ' ')"
    }
}
finally {
    if (Test-Path -LiteralPath $portableSmokeRoot) {
        Remove-Item -LiteralPath $portableSmokeRoot -Recurse -Force
    }
}

$sbomPath = Join-Path $artifacts "Speak-$Version.spdx.json"
$sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
if (($sbom.PSObject.Properties.Name -notcontains "spdxVersion") -or
    ([string]$sbom.spdxVersion -cne "SPDX-2.2")) {
    throw "Release SBOM is not an SPDX 2.2 document."
}

$sbomPackages = @($sbom.packages)
$rootPackages = @($sbomPackages | Where-Object {
    ([string]$_.name -ceq "Speak") -and
    ([string]$_.versionInfo -ceq $Version)
})
if ($rootPackages.Count -ne 1 -or $sbomPackages.Count -le 1) {
    throw "Release SBOM must contain Speak $Version and its detected dependencies."
}
if (@($sbom.files).Count -le 0) {
    throw "Release SBOM does not inventory the published files."
}
$edgePinnedRequirements = Get-EdgeTtsPinnedRequirements -Path $edgeRequirementsPath
$edgeWheelManifest = Read-CompleteJson -Path $edgeManifestPath -Description "Tracked Edge TTS wheel manifest"
if ([string]$edgeWheelManifest.requirementsSha256 -cne (Get-CompleteSha256 -Path $edgeRequirementsPath) -or
    @($edgeWheelManifest.files).Count -ne $edgePinnedRequirements.Count) {
    throw "The tracked Edge TTS wheel manifest does not bind to the exact requirement lock."
}

$packagesByNormalizedName = @{}
foreach ($package in $sbomPackages) {
    $normalized = ([string]$package.name).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        throw "Release SBOM contains a package with no name."
    }
    if (-not $packagesByNormalizedName.ContainsKey($normalized)) {
        $packagesByNormalizedName[$normalized] = [System.Collections.Generic.List[object]]::new()
    }
    $packagesByNormalizedName[$normalized].Add($package)
}

$pythonPackages = @(if ($packagesByNormalizedName.ContainsKey('cpython')) {
    $packagesByNormalizedName['cpython']
})
if ($pythonPackages.Count -ne 1 -or
    [string]$pythonPackages[0].versionInfo -cne [string]$sourceLock.python.version -or
    [string]$pythonPackages[0].licenseDeclared -cne 'PSF-2.0') {
    throw "Release SBOM must include exactly the audited CPython $($sourceLock.python.version) package."
}
$pythonChecksums = @($pythonPackages[0].checksums | Where-Object { [string]$_.algorithm -ceq 'SHA256' })
if ($pythonChecksums.Count -ne 1 -or
    [string]$pythonChecksums[0].checksumValue -cne [string]$sourceLock.python.sha256) {
    throw "Release SBOM CPython checksum does not match the audited source lock."
}
$pythonRelationships = @($sbom.relationships | Where-Object {
    [string]$_.spdxElementId -ceq 'SPDXRef-RootPackage' -and
    [string]$_.relationshipType -ceq 'DEPENDS_ON' -and
    [string]$_.relatedSpdxElement -ceq [string]$pythonPackages[0].SPDXID
})
if ($pythonRelationships.Count -ne 1) {
    throw "Release SBOM must link Speak to CPython with exactly one DEPENDS_ON relationship."
}

foreach ($wheel in @($edgeWheelManifest.files)) {
    $normalized = ([string]$wheel.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
    if (-not $packagesByNormalizedName.ContainsKey($normalized) -or
        @($packagesByNormalizedName[$normalized]).Count -ne 1) {
        throw "Release SBOM must contain exactly one package for $($wheel.distribution)."
    }
    $package = @($packagesByNormalizedName[$normalized])[0]
    if ([string]$package.versionInfo -cne [string]$wheel.version -or
        [string]$package.licenseDeclared -cne [string]$wheel.licenseDeclared -or
        [string]$package.licenseDeclared -ceq 'NOASSERTION') {
        throw "Release SBOM version or declared license mismatch for $($wheel.distribution)."
    }
    $checksums = @($package.checksums | Where-Object { [string]$_.algorithm -ceq 'SHA256' })
    if ($checksums.Count -ne 1 -or [string]$checksums[0].checksumValue -cne [string]$wheel.sha256) {
        throw "Release SBOM checksum mismatch for $($wheel.distribution)."
    }
    $dependencyRelationships = @($sbom.relationships | Where-Object {
        [string]$_.spdxElementId -ceq 'SPDXRef-RootPackage' -and
        [string]$_.relationshipType -ceq 'DEPENDS_ON' -and
        [string]$_.relatedSpdxElement -ceq [string]$package.SPDXID
    })
    if ($dependencyRelationships.Count -ne 1) {
        throw "Release SBOM must link Speak to $($wheel.distribution) with exactly one DEPENDS_ON relationship."
    }
}

Write-Host "Release asset verification passed: $artifacts"
