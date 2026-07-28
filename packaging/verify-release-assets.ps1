param(
    [string]$ArtifactsRoot = (Join-Path $PSScriptRoot "artifacts"),
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = [System.IO.Path]::GetFullPath($ArtifactsRoot)

if (-not $Version) {
    [xml]$buildProperties = Get-Content -LiteralPath `
        (Join-Path $repoRoot "Directory.Build.props") -Raw
    $Version = [string]$buildProperties.Project.PropertyGroup.VersionPrefix
}

if (-not $Version -or $Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "A valid release version is required. Received: '$Version'."
}

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
    "Speak-$Version.spdx.json",
    "SHA256SUMS.txt"
)
$expectedAssetNames = @($expectedNames | Where-Object { $_ -cne "SHA256SUMS.txt" })

$actualFiles = @(Get-ChildItem -LiteralPath $artifacts -Recurse -File)
$actualNames = @($actualFiles | ForEach-Object {
    $relative = $_.FullName.Substring($artifacts.Length).TrimStart('\', '/')
    $relative.Replace('\', '/')
} | Sort-Object)
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

Write-Host "Release asset verification passed: $artifacts"
