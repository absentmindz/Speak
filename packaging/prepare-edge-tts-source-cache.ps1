[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DestinationRoot,
    [switch]$IncludeCompleteInstallerSources
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'complete\CompleteInstaller.Common.ps1')
. (Join-Path $PSScriptRoot 'EdgeTtsRuntime.Common.ps1')

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourceLockPath = Join-Path $PSScriptRoot 'complete\sources.lock.json'
$edgeManifestPath = Join-Path $PSScriptRoot 'edge-tts-wheelhouse.manifest.json'
$edgeRequirementsPath = Join-Path $repoRoot 'tools\requirements-edge-tts.lock.txt'
$whisperManifestPath = Join-Path $PSScriptRoot 'complete\whisper-cpu-wheelhouse.manifest.json'
$whisperRequirementsPath = Join-Path $repoRoot 'tools\requirements-whisper-cpu.lock.txt'

function Save-AuditedSourceFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceUrl,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,
        [Parameter(Mandatory = $true)]
        [Int64]$ExpectedSizeBytes,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedSha256,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ($SourceUrl -notmatch '^https://') {
        throw "$Description must use an HTTPS source URL: $SourceUrl"
    }
    $destination = [System.IO.Path]::GetFullPath($DestinationPath)
    Assert-CompleteNoReparseAncestors -Path $destination -Description "$Description destination"
    if (Test-Path -LiteralPath $destination) {
        Assert-CompleteExactFile -Path $destination -Description $Description `
            -ExpectedSizeBytes $ExpectedSizeBytes -ExpectedSha256 $ExpectedSha256 | Out-Null
        Write-Host "Verified cached source: $destination"
        return
    }

    $parent = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Assert-CompleteNoReparseAncestors -Path $parent -Description "$Description destination directory"
    $temporary = $destination + '.download-' + [Guid]::NewGuid().ToString('N')
    try {
        Invoke-WebRequest -Uri $SourceUrl -OutFile $temporary -UseBasicParsing
        Assert-CompleteExactFile -Path $temporary -Description "$Description download" `
            -ExpectedSizeBytes $ExpectedSizeBytes -ExpectedSha256 $ExpectedSha256 | Out-Null
        Move-Item -LiteralPath $temporary -Destination $destination
        Assert-CompleteExactFile -Path $destination -Description $Description `
            -ExpectedSizeBytes $ExpectedSizeBytes -ExpectedSha256 $ExpectedSha256 | Out-Null
        Write-Host "Downloaded and verified: $destination"
    }
    finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

$cacheRoot = [System.IO.Path]::GetFullPath($DestinationRoot)
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
Assert-CompleteNoReparseAncestors -Path $cacheRoot -Description 'Edge TTS source cache'

$sourceLock = Read-CompleteJson -Path $sourceLockPath -Description 'Complete-installer source lock'
if ([int]$sourceLock.schemaVersion -ne 1 -or
    [string]$sourceLock.python.version -cne '3.14.6' -or
    [string]$sourceLock.edgeTts.version -cne '7.2.8') {
    throw 'The source lock does not describe the audited Python 3.14.6 / edge-tts 7.2.8 baseline.'
}

$edgePinnedRequirements = Get-EdgeTtsPinnedRequirements -Path $edgeRequirementsPath
$edgeManifest = Read-CompleteJson -Path $edgeManifestPath -Description 'Tracked Edge TTS wheel manifest'
if ([string]$edgeManifest.requirementsSha256 -cne (Get-CompleteSha256 -Path $edgeRequirementsPath)) {
    throw 'The tracked Edge TTS wheel manifest does not bind to the current requirement lock.'
}

Save-AuditedSourceFile -SourceUrl ([string]$sourceLock.python.sourceUrl) `
    -DestinationPath (Join-Path $cacheRoot ([string]$sourceLock.python.fileName)) `
    -ExpectedSizeBytes ([Int64]$sourceLock.python.sizeBytes) `
    -ExpectedSha256 ([string]$sourceLock.python.sha256) `
    -Description 'Python 3.14.6 embedded archive'
Save-AuditedSourceFile -SourceUrl ([string]$sourceLock.python.sigstore.sourceUrl) `
    -DestinationPath (Join-Path $cacheRoot ([string]$sourceLock.python.sigstore.fileName)) `
    -ExpectedSizeBytes ([Int64]$sourceLock.python.sigstore.sizeBytes) `
    -ExpectedSha256 ([string]$sourceLock.python.sigstore.sha256) `
    -Description 'Python 3.14.6 Sigstore bundle'

$pythonArchive = Join-Path $cacheRoot ([string]$sourceLock.python.fileName)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$pythonZip = [System.IO.Compression.ZipFile]::OpenRead($pythonArchive)
try {
    $licenseEntry = @($pythonZip.Entries | Where-Object {
        ([string]$_.FullName).Replace('\', '/') -ceq [string]$sourceLock.python.license.relativePath
    })
    if ($licenseEntry.Count -ne 1 -or [Int64]$licenseEntry[0].Length -ne [Int64]$sourceLock.python.license.sizeBytes) {
        throw 'The Python 3.14.6 archive does not contain the exact locked CPython license entry.'
    }
    $licenseStream = $licenseEntry[0].Open()
    $licenseAlgorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $licenseHash = ([System.BitConverter]::ToString($licenseAlgorithm.ComputeHash($licenseStream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $licenseAlgorithm.Dispose()
        $licenseStream.Dispose()
    }
    if ($licenseHash -cne [string]$sourceLock.python.license.sha256) {
        throw 'The Python 3.14.6 archive CPython license SHA-256 does not match sources.lock.json.'
    }
}
finally {
    $pythonZip.Dispose()
}

$wheelRoot = Join-Path $cacheRoot 'edge-tts-wheelhouse'
New-Item -ItemType Directory -Path $wheelRoot -Force | Out-Null
Assert-CompleteNoReparseAncestors -Path $wheelRoot -Description 'Edge TTS wheel cache'
foreach ($wheel in @($edgeManifest.files)) {
    $relative = ([string]$wheel.relativePath).Replace('\', '/')
    Assert-CompleteSafeRelativePath -RelativePath $relative -Description 'Edge TTS wheel manifest'
    if ($relative -match '[\\/]') {
        throw "The Edge TTS wheel cache must remain flat: $relative"
    }
    Save-AuditedSourceFile -SourceUrl ([string]$wheel.sourceUrl) `
        -DestinationPath (Join-Path $wheelRoot $relative) `
        -ExpectedSizeBytes ([Int64]$wheel.sizeBytes) `
        -ExpectedSha256 ([string]$wheel.sha256) `
        -Description "Edge TTS wheel $($wheel.distribution)==$($wheel.version)"
}

Assert-EdgeTtsWheelhouse -WheelRoot $wheelRoot -ManifestPath $edgeManifestPath `
    -RequirementsPath $edgeRequirementsPath -PinnedRequirements $edgePinnedRequirements | Out-Null

$cachedEdgeManifest = Join-Path $cacheRoot 'edge-tts-wheelhouse.manifest.json'
Copy-Item -LiteralPath $edgeManifestPath -Destination $cachedEdgeManifest -Force
if ((Get-CompleteSha256 -Path $cachedEdgeManifest) -cne (Get-CompleteSha256 -Path $edgeManifestPath)) {
    throw 'The cached Edge TTS wheel manifest differs from the tracked audited manifest.'
}

if ($IncludeCompleteInstallerSources) {
    $completeSources = @(
        @{ Lock = $sourceLock.whisperBase; Description = 'OpenAI Whisper base checkpoint' },
        @{ Lock = $sourceLock.ffmpeg.archive; Description = 'Audited FFmpeg archive' },
        @{ Lock = $sourceLock.vcRedist; Description = 'Microsoft Visual C++ Redistributable' }
    )
    foreach ($source in $completeSources) {
        Save-AuditedSourceFile -SourceUrl ([string]$source.Lock.sourceUrl) `
            -DestinationPath (Join-Path $cacheRoot ([string]$source.Lock.fileName)) `
            -ExpectedSizeBytes ([Int64]$source.Lock.sizeBytes) `
            -ExpectedSha256 ([string]$source.Lock.sha256) `
            -Description ([string]$source.Description)
    }

    Assert-CompleteFile -Path $whisperRequirementsPath -Description 'Whisper CPU requirement lock' | Out-Null
    $whisperManifest = Read-CompleteJson -Path $whisperManifestPath -Description 'Tracked Whisper CPU wheel manifest'
    if ([int]$whisperManifest.schemaVersion -ne 1 -or
        [string]$whisperManifest.pythonVersion -cne '3.14.6' -or
        [string]$whisperManifest.requirementsSha256 -cne (Get-CompleteSha256 -Path $whisperRequirementsPath) -or
        @($whisperManifest.files).Count -eq 0) {
        throw 'The tracked Whisper CPU wheel manifest does not bind to the current Python 3.14.6 requirement lock.'
    }

    $whisperWheelRoot = Join-Path $cacheRoot 'whisper-cpu-wheelhouse'
    New-Item -ItemType Directory -Path $whisperWheelRoot -Force | Out-Null
    Assert-CompleteNoReparseAncestors -Path $whisperWheelRoot -Description 'Whisper CPU wheel cache'
    foreach ($wheel in @($whisperManifest.files)) {
        $relative = ([string]$wheel.relativePath).Replace('\', '/')
        Assert-CompleteSafeRelativePath -RelativePath $relative -Description 'Whisper CPU wheel manifest'
        if ($relative -match '[\\/]' -or $relative -notmatch '\.whl$') {
            throw "The Whisper CPU wheel cache must remain flat and contain wheels only: $relative"
        }
        Save-AuditedSourceFile -SourceUrl ([string]$wheel.sourceUrl) `
            -DestinationPath (Join-Path $whisperWheelRoot $relative) `
            -ExpectedSizeBytes ([Int64]$wheel.sizeBytes) `
            -ExpectedSha256 ([string]$wheel.sha256) `
            -Description "Whisper CPU wheel $($wheel.distribution)==$($wheel.version)"
    }
    Assert-CompleteExpectedFileSet -RootPath $whisperWheelRoot -ExpectedFiles @($whisperManifest.files) `
        -Description 'Whisper CPU wheel cache'

    $cachedWhisperManifest = Join-Path $cacheRoot 'whisper-cpu-wheelhouse.manifest.json'
    Copy-Item -LiteralPath $whisperManifestPath -Destination $cachedWhisperManifest -Force
    if ((Get-CompleteSha256 -Path $cachedWhisperManifest) -cne (Get-CompleteSha256 -Path $whisperManifestPath)) {
        throw 'The cached Whisper CPU wheel manifest differs from the tracked audited manifest.'
    }
}

$scope = if ($IncludeCompleteInstallerSources) { 'Standard and Complete installer' } else { 'Edge TTS' }
Write-Host "$scope audited source cache is ready: $cacheRoot"
