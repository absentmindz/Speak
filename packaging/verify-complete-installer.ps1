[CmdletBinding()]
param(
    [string]$StageRoot = (Join-Path $PSScriptRoot "stage\complete"),
    [string]$InstallerPath = "",
    [string]$Version = "",
    [switch]$SkipRuntimeHealth
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "complete\CompleteInstaller.Common.ps1")
. (Join-Path $PSScriptRoot "EdgeTtsRuntime.Common.ps1")

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Get-ExpectedCompleteVersion {
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        return $Version
    }

    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw
    return [string]$buildProperties.Project.PropertyGroup.VersionPrefix
}

function Assert-CompleteManifestInventory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [object[]]$Entries
    )

    $root = Assert-CompleteDirectory -Path $RootPath -Description "Complete installer staged payload"
    $expected = @{}
    foreach ($entry in $Entries) {
        foreach ($property in @('component', 'relativePath', 'sizeBytes', 'sha256')) {
            if ($null -eq $entry.PSObject.Properties[$property] -or [string]::IsNullOrWhiteSpace([string]$entry.$property)) {
                throw "Component manifest contains an incomplete file entry. Missing $property."
            }
        }

        $relative = ([string]$entry.relativePath).Replace('\', '/')
        Assert-CompleteSafeRelativePath -RelativePath $relative -Description "Component manifest"
        if ($relative -ceq 'component-manifest.json') {
            throw "Component manifest must not self-inventory its own mutable file."
        }
        if ($expected.ContainsKey($relative)) {
            throw "Component manifest contains a duplicate file entry: $relative"
        }
        $expected[$relative] = $entry
    }

    $actual = @{}
    foreach ($file in @(Get-CompleteTreeFiles -RootPath $root.FullName -Description "Complete installer staged payload")) {
        $relative = Get-CompleteRelativePath -BasePath $root.FullName -TargetPath $file.FullName
        $actual[$relative] = $file
    }

    if (-not $actual.ContainsKey('component-manifest.json')) {
        throw "Complete installer stage is missing component-manifest.json."
    }
    $actual.Remove('component-manifest.json')

    $expectedNames = @($expected.Keys | Sort-Object)
    $actualNames = @($actual.Keys | Sort-Object)
    if (($expectedNames.Count -ne $actualNames.Count) -or
        (($expectedNames -join "`n") -cne ($actualNames -join "`n"))) {
        throw "Complete installer component inventory has missing or unexpected files. Expected:`n$($expectedNames -join [Environment]::NewLine)`nFound:`n$($actualNames -join [Environment]::NewLine)"
    }

    foreach ($relative in $expectedNames) {
        $entry = $expected[$relative]
        Assert-CompleteExactFile -Path $actual[$relative].FullName -Description "Staged component $relative" `
            -ExpectedSizeBytes ([Int64]$entry.sizeBytes) -ExpectedSha256 ([string]$entry.sha256) -AllowEmpty | Out-Null
    }
}

function Assert-CompleteAppSettings {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SettingsPath
    )

    $settings = Read-CompleteJson -Path $SettingsPath -Description "Complete installer appsettings"
    $expectations = @(
        @{ Path = 'Paths.ToolsRoot'; Value = '{AppDir}' },
        @{ Path = 'Paths.ModelsRoot'; Value = '' },
        @{ Path = 'Transcription.DefaultEngine'; Value = 'whisper-local' },
        @{ Path = 'Transcription.DefaultModel'; Value = 'whisper-base' },
        @{ Path = 'Transcription.DefaultDevice'; Value = 'auto' },
        @{ Path = 'Transcription.WhisperPythonPath'; Value = '{AppDir}\Runtime\Whisper\python.exe' },
        @{ Path = 'Transcription.WhisperWrapperPath'; Value = '{AppDir}\Tools\speak_worker.py' },
        @{ Path = 'Transcription.WhisperModelPath'; Value = '{ModelsRoot}\whisper\base.pt' },
        @{ Path = 'Transcription.WhisperServerScriptPath'; Value = '{AppDir}\Tools\whisper_resident_server.py' },
        @{ Path = 'TTS.EdgeTtsExecutablePath'; Value = '' }
    )

    foreach ($expectation in $expectations) {
        $segments = $expectation.Path.Split('.')
        $current = $settings
        foreach ($segment in $segments) {
            if ($null -eq $current -or $null -eq $current.PSObject.Properties[$segment]) {
                throw "Complete installer appsettings is missing $($expectation.Path)."
            }
            $current = $current.$segment
        }
        if ([string]$current -cne [string]$expectation.Value) {
            throw "Complete installer appsettings has an unexpected value for $($expectation.Path): '$current'"
        }
    }
}

function Assert-StagedVcRedistVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    $actualVersion = ([string](Get-Item -LiteralPath $Path).VersionInfo.ProductVersion).Trim()
    if ($actualVersion -cne $ExpectedVersion) {
        throw "Staged Microsoft Visual C++ Redistributable version mismatch. Expected $ExpectedVersion; found $actualVersion."
    }
}

function Assert-StagedFfmpegVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BinRoot,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    $ffmpeg = Join-Path $BinRoot 'ffmpeg.exe'
    $output = @(& $ffmpeg -hide_banner -version 2>&1)
    if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) {
        throw "Staged FFmpeg executable/DLL health check failed: $($output -join [Environment]::NewLine)"
    }
    $firstLine = ([string]$output[0]).Trim()
    if ($firstLine -notmatch ('^ffmpeg version ' + [regex]::Escape($ExpectedVersion) + '(?:\s|$)')) {
        throw "Staged FFmpeg version mismatch. Expected $ExpectedVersion; found '$firstLine'."
    }
}

function Assert-CompleteInstallerHeader {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    $installer = Assert-CompleteFile -Path $Path -Description "Complete offline installer"
    if ($installer.Length -le 0) {
        throw "Complete offline installer is empty."
    }

    $stream = [System.IO.File]::OpenRead($installer.FullName)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "Complete offline installer does not have an MZ executable header."
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt ($stream.Length - 4)) {
            throw "Complete offline installer has an invalid PE header offset."
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Complete offline installer does not have a PE header."
        }
    }
    finally {
        $stream.Dispose()
    }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installer.FullName)
    if (([string]$versionInfo.ProductVersion).Trim() -cne $ExpectedVersion) {
        throw "Complete offline installer product version does not match $ExpectedVersion."
    }
    if (([string]$versionInfo.FileVersion).Trim() -ne '' -and
        ([string]$versionInfo.FileVersion).Trim() -notmatch ('^' + [regex]::Escape($ExpectedVersion) + '(?:\.|\s|$)')) {
        throw "Complete offline installer file version does not start with $ExpectedVersion."
    }
}

$expectedVersion = Get-ExpectedCompleteVersion
if (-not $expectedVersion -or $expectedVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "A valid complete-installer version is required. Received: '$expectedVersion'."
}

$stage = (Assert-CompleteDirectory -Path $StageRoot -Description "Complete installer staged payload").FullName
Assert-CompleteTreeNoReparsePoints -RootPath $stage -Description "Complete installer staged payload"
$componentManifestPath = Join-Path $stage "component-manifest.json"
$componentManifest = Read-CompleteJson -Path $componentManifestPath -Description "Complete installer component manifest"
if ([int]$componentManifest.schemaVersion -ne 1 -or
    [string]$componentManifest.installerKind -cne 'complete-offline-cpu' -or
    [string]$componentManifest.appVersion -cne $expectedVersion) {
    throw "Complete installer component manifest has an unsupported schema, installer kind, or version."
}
if ([string]$componentManifest.runtime.pythonVersion -cne '3.14.6' -or
    [string]$componentManifest.runtime.edgeTts -cne '7.2.8' -or
    [string]$componentManifest.runtime.modelId -cne 'whisper-base' -or
    [string]$componentManifest.runtime.defaultDevice -cne 'auto') {
    throw "Complete installer component manifest does not describe the required CPU Whisper baseline."
}

$inventory = @($componentManifest.files)
if ($inventory.Count -eq 0) {
    throw "Complete installer component manifest does not inventory any payload files."
}
Assert-CompleteManifestInventory -RootPath $stage -Entries $inventory

$appRoot = Join-Path $stage "App"
foreach ($file in @('Speak.exe', 'Speak.deps.json', 'Speak.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'Tools\speak_worker.py')) {
    Assert-CompleteFile -Path (Join-Path $appRoot $file) -Description "Complete application file $file" | Out-Null
}
foreach ($licenseFile in @(
    'Runtime\Whisper\LICENSE.txt',
    'Licenses\THIRD-PARTY-NOTICES.txt',
    'Licenses\FFmpeg-NOTICE.txt',
    'Licenses\FFmpeg-LGPL-3.0.txt',
    'Licenses\Whisper-MIT-LICENSE.txt'
)) {
    $licenseItem = Assert-CompleteFile -Path (Join-Path $stage $licenseFile) -Description "Complete installer license file $licenseFile"
    if ($licenseItem.Length -le 0) {
        throw "Complete installer license file is empty: $licenseFile"
    }
}
$trackedComplianceFiles = @(
    @{ Staged = 'App\LICENSE'; Tracked = Join-Path $repoRoot 'LICENSE' },
    @{ Staged = 'App\NOTICE'; Tracked = Join-Path $repoRoot 'NOTICE' },
    @{ Staged = 'App\THIRD-PARTY-NOTICES.txt'; Tracked = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt' },
    @{ Staged = 'Licenses\THIRD-PARTY-NOTICES.txt'; Tracked = Join-Path $PSScriptRoot 'complete\Complete-THIRD-PARTY-NOTICES.txt' },
    @{ Staged = 'Licenses\FFmpeg-NOTICE.txt'; Tracked = Join-Path $PSScriptRoot 'complete\FFmpeg-NOTICE-complete.txt' },
    @{ Staged = 'Licenses\FFmpeg-LGPL-3.0.txt'; Tracked = Join-Path $PSScriptRoot 'complete\FFmpeg-LGPL-3.0.txt' },
    @{ Staged = 'Licenses\Whisper-MIT-LICENSE.txt'; Tracked = Join-Path $PSScriptRoot 'model-licenses\Whisper-MIT-LICENSE.txt' }
)
foreach ($compliance in $trackedComplianceFiles) {
    $stagedCompliance = Join-Path $stage $compliance.Staged
    if ((Get-CompleteSha256 -Path $stagedCompliance) -cne (Get-CompleteSha256 -Path $compliance.Tracked)) {
        throw "Complete installer compliance file differs from its tracked source: $($compliance.Staged)"
    }
}
Assert-CompleteAppSettings -SettingsPath (Join-Path $appRoot "appsettings.json")

$sourceLockPath = Join-Path $stage "Support\sources.lock.json"
$sourceLock = Read-CompleteJson -Path $sourceLockPath -Description "Staged complete installer source lock"
if ([int]$sourceLock.schemaVersion -ne 1 -or
    [string]$sourceLock.python.version -cne '3.14.6' -or
    [string]$sourceLock.python.license.relativePath -cne 'LICENSE.txt' -or
    [string]$sourceLock.edgeTts.version -cne '7.2.8' -or
    [string]$sourceLock.whisperBase.modelId -cne 'whisper-base') {
    throw "Staged source lock does not match the required complete installer baseline."
}
if ((Get-CompleteSha256 -Path $sourceLockPath) -cne [string]$componentManifest.sourceLockSha256) {
    throw "The staged source lock does not match the source lock hash recorded in component-manifest.json."
}

$modelPath = Join-Path $stage "Models\whisper\base.pt"
Assert-CompleteExactFile -Path $modelPath -Description "Staged Whisper base model" `
    -ExpectedSizeBytes ([Int64]$sourceLock.whisperBase.sizeBytes) -ExpectedSha256 ([string]$sourceLock.whisperBase.sha256) | Out-Null
$ffmpegRoot = Join-Path $stage "Ffmpeg\bin"
Assert-CompleteExpectedFileSet -RootPath $ffmpegRoot -ExpectedFiles @($sourceLock.ffmpeg.files) -Description "Staged FFmpeg executable and DLL payload"
$vcRedist = Join-Path $stage ("Support\" + [string]$sourceLock.vcRedist.fileName)
Assert-CompleteExactFile -Path $vcRedist -Description "Staged Microsoft Visual C++ Redistributable" `
    -ExpectedSizeBytes ([Int64]$sourceLock.vcRedist.sizeBytes) -ExpectedSha256 ([string]$sourceLock.vcRedist.sha256) | Out-Null
Assert-CompleteAuthenticodeSignature -Path $vcRedist -Description "Staged Microsoft Visual C++ Redistributable" `
    -SubjectRegex ([string]$sourceLock.vcRedist.signature.subjectRegex) -IssuerRegex ([string]$sourceLock.vcRedist.signature.issuerRegex)
Assert-StagedVcRedistVersion -Path $vcRedist -ExpectedVersion ([string]$sourceLock.vcRedist.version)

$requirementsPath = Join-Path $stage "Support\requirements-whisper-cpu.lock.txt"
$wheelManifestPath = Join-Path $stage "Support\whisper-cpu-wheelhouse.manifest.json"
Assert-CompleteFile -Path $requirementsPath -Description "Staged Whisper CPU requirement lock" | Out-Null
$wheelManifest = Read-CompleteJson -Path $wheelManifestPath -Description "Staged Whisper CPU wheel cache manifest"
if ([int]$wheelManifest.schemaVersion -ne 1 -or [string]$wheelManifest.pythonVersion -cne '3.14.6' -or
    [string]$wheelManifest.requirementsSha256 -cne (Get-CompleteSha256 -Path $requirementsPath) -or
    @($wheelManifest.files).Count -eq 0) {
    throw "Staged Whisper CPU wheel cache manifest does not bind to the Python 3.14.6 requirement lock or lacks its audited wheel inventory."
}
$trackedWhisperManifestPath = Join-Path $PSScriptRoot 'complete\whisper-cpu-wheelhouse.manifest.json'
if ((Get-CompleteSha256 -Path $wheelManifestPath) -cne (Get-CompleteSha256 -Path $trackedWhisperManifestPath)) {
    throw "Staged Whisper CPU wheel manifest differs from the tracked audited manifest."
}
foreach ($wheelEntry in @($wheelManifest.files)) {
    if ($null -eq $wheelEntry.PSObject.Properties['licenseDeclared'] -or
        [string]::IsNullOrWhiteSpace([string]$wheelEntry.licenseDeclared) -or
        [string]$wheelEntry.licenseDeclared -ceq 'NOASSERTION') {
        throw "Staged Whisper CPU wheel manifest lacks an audited license for $($wheelEntry.distribution)."
    }
}
$wheelDistributions = @($wheelManifest.files | ForEach-Object {
    ([string]$_.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
})
if ($wheelDistributions -contains 'pip') {
    throw "The customer runtime must not ship pip or other package-management entry points."
}
if ($wheelDistributions -notcontains 'setuptools') {
    throw "The staged CPU wheel inventory is missing the pinned setuptools runtime dependency."
}

$edgeRequirementsPath = Join-Path $stage "Support\requirements-edge-tts.lock.txt"
$edgeWheelManifestPath = Join-Path $stage "Support\edge-tts-wheelhouse.manifest.json"
Assert-CompleteFile -Path $edgeRequirementsPath -Description "Staged Edge TTS requirement lock" | Out-Null
$edgePinnedRequirements = Get-EdgeTtsPinnedRequirements -Path $edgeRequirementsPath
$edgeWheelManifest = Read-CompleteJson -Path $edgeWheelManifestPath -Description "Staged Edge TTS wheel cache manifest"
if ([int]$edgeWheelManifest.schemaVersion -ne 1 -or [string]$edgeWheelManifest.pythonVersion -cne '3.14.6' -or
    [string]$edgeWheelManifest.requirementsSha256 -cne (Get-CompleteSha256 -Path $edgeRequirementsPath) -or
    @($edgeWheelManifest.files).Count -ne $edgePinnedRequirements.Count) {
    throw "Staged Edge TTS wheel cache manifest does not bind to the Python 3.14.6 requirement lock."
}
$trackedEdgeManifestPath = Join-Path $PSScriptRoot 'edge-tts-wheelhouse.manifest.json'
if ((Get-CompleteSha256 -Path $edgeWheelManifestPath) -cne (Get-CompleteSha256 -Path $trackedEdgeManifestPath)) {
    throw "Staged Edge TTS wheel manifest differs from the tracked audited manifest."
}
$edgeManifestNames = @($edgeWheelManifest.files | ForEach-Object {
    ([string]$_.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
})
foreach ($requirement in $edgePinnedRequirements) {
    if ($edgeManifestNames -notcontains $requirement.NormalizedName) {
        throw "Staged Edge TTS wheel manifest is missing $($requirement.Name)==$($requirement.Version)."
    }
}
if ($edgeManifestNames -contains 'pip') {
    throw "The Edge TTS runtime must not ship pip."
}

$spdxPath = Join-Path $stage "Support\Speak-$expectedVersion-Complete-Offline.spdx.json"
$spdx = Read-CompleteJson -Path $spdxPath -Description "Complete offline CPU SPDX SBOM"
if ([string]$spdx.spdxVersion -cne 'SPDX-2.2' -or
    [string]$spdx.dataLicense -cne 'CC0-1.0' -or
    [string]$spdx.SPDXID -cne 'SPDXRef-DOCUMENT' -or
    [string]$spdx.name -cne "Speak-$expectedVersion-Complete-Offline-CPU" -or
    @($spdx.documentDescribes) -notcontains 'SPDXRef-Package-Speak') {
    throw "Complete offline CPU SBOM does not declare the required SPDX 2.3 document metadata."
}
$spdxPackagesByName = @{}
foreach ($package in @($spdx.packages)) {
    $normalizedName = ([string]$package.name).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
    if ([string]::IsNullOrWhiteSpace($normalizedName) -or $spdxPackagesByName.ContainsKey($normalizedName)) {
        throw "Complete offline CPU SBOM contains a missing or duplicate package name."
    }
    if ([string]::IsNullOrWhiteSpace([string]$package.SPDXID) -or
        [string]::IsNullOrWhiteSpace([string]$package.versionInfo) -or
        [string]::IsNullOrWhiteSpace([string]$package.licenseDeclared)) {
        throw "Complete offline CPU SBOM contains an incomplete package entry for $($package.name)."
    }
    $spdxPackagesByName[$normalizedName] = $package
}
foreach ($requiredComponent in @(
    'speak',
    'cpython',
    'ffmpeg',
    'openai-whisper-base-model',
    'microsoft-visual-cpp-redistributable-x64'
)) {
    if (-not $spdxPackagesByName.ContainsKey($requiredComponent)) {
        throw "Complete offline CPU SBOM is missing required component $requiredComponent."
    }
}
$validatedPackageIds = @{}
foreach ($wheelEntry in @($wheelManifest.files) + @($edgeWheelManifest.files)) {
    $normalizedName = ([string]$wheelEntry.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
    if (-not $spdxPackagesByName.ContainsKey($normalizedName)) {
        throw "Complete offline CPU SBOM is missing Python distribution $($wheelEntry.distribution)."
    }
    $package = $spdxPackagesByName[$normalizedName]
    if ([string]$package.versionInfo -cne [string]$wheelEntry.version -or
        [string]$package.licenseDeclared -cne [string]$wheelEntry.licenseDeclared -or
        [string]$package.licenseDeclared -ceq 'NOASSERTION') {
        throw "Complete offline CPU SBOM version or declared-license mismatch for $($wheelEntry.distribution)."
    }
    $sha256Checksums = @($package.checksums | Where-Object { [string]$_.algorithm -ceq 'SHA256' })
    if ($sha256Checksums.Count -ne 1 -or
        [string]$sha256Checksums[0].checksumValue -cne [string]$wheelEntry.sha256) {
        throw "Complete offline CPU SBOM checksum mismatch for $($wheelEntry.distribution)."
    }
    if (-not $validatedPackageIds.ContainsKey([string]$package.SPDXID)) {
        $relationships = @($spdx.relationships | Where-Object {
            [string]$_.spdxElementId -ceq 'SPDXRef-Package-Speak' -and
            [string]$_.relationshipType -ceq 'DEPENDS_ON' -and
            [string]$_.relatedSpdxElement -ceq [string]$package.SPDXID
        })
        if ($relationships.Count -ne 1) {
            throw "Complete offline CPU SBOM must link Speak to $($wheelEntry.distribution) with exactly one DEPENDS_ON relationship."
        }
        $validatedPackageIds[[string]$package.SPDXID] = $true
    }
}

$fixedSbomComponents = @(
    @{ Name = 'cpython'; Version = [string]$sourceLock.python.version; License = 'PSF-2.0'; Sha256 = [string]$sourceLock.python.sha256 },
    @{ Name = 'ffmpeg'; Version = [string]$sourceLock.ffmpeg.version; License = [string]$sourceLock.ffmpeg.license; Sha256 = [string]$sourceLock.ffmpeg.archive.sha256 },
    @{ Name = 'openai-whisper-base-model'; Version = [string]$sourceLock.whisperBase.modelId; License = 'MIT'; Sha256 = [string]$sourceLock.whisperBase.sha256 },
    @{ Name = 'microsoft-visual-cpp-redistributable-x64'; Version = [string]$sourceLock.vcRedist.version; License = 'LicenseRef-Microsoft-Visual-Cpp-Redistributable'; Sha256 = [string]$sourceLock.vcRedist.sha256 }
)
foreach ($component in $fixedSbomComponents) {
    $package = $spdxPackagesByName[$component.Name]
    if ([string]$package.versionInfo -cne $component.Version -or [string]$package.licenseDeclared -cne $component.License) {
        throw "Complete offline CPU SBOM metadata mismatch for $($component.Name)."
    }
    $checksums = @($package.checksums | Where-Object { [string]$_.algorithm -ceq 'SHA256' })
    if ($checksums.Count -ne 1 -or [string]$checksums[0].checksumValue -cne $component.Sha256) {
        throw "Complete offline CPU SBOM checksum mismatch for $($component.Name)."
    }
    $relationships = @($spdx.relationships | Where-Object {
        [string]$_.spdxElementId -ceq 'SPDXRef-Package-Speak' -and
        [string]$_.relationshipType -ceq 'DEPENDS_ON' -and
        [string]$_.relatedSpdxElement -ceq [string]$package.SPDXID
    })
    if ($relationships.Count -ne 1) {
        throw "Complete offline CPU SBOM must link Speak to $($component.Name) with exactly one DEPENDS_ON relationship."
    }
}

$python = Join-Path $stage "Runtime\Whisper\python.exe"
Assert-CompleteFile -Path $python -Description "Staged embedded Python executable" | Out-Null
$pthFiles = @(Get-ChildItem -LiteralPath (Split-Path -Parent $python) -Filter "python*._pth" -File -Force)
if ($pthFiles.Count -ne 1) {
    throw "Staged embedded Python runtime must contain exactly one python*._pth file."
}
$pth = Get-Content -LiteralPath $pthFiles[0].FullName -Raw
foreach ($requiredPthLine in @('Lib\site-packages', 'import site')) {
    if ($pth -notmatch ('(?m)^' + [regex]::Escape($requiredPthLine) + '\r?$')) {
        throw "Staged embedded Python runtime does not enable $requiredPthLine in $($pthFiles[0].Name)."
    }
}
$runtimeRoot = Split-Path -Parent $python
$pythonLicense = Join-Path $runtimeRoot 'LICENSE.txt'
Assert-CompleteExactFile -Path $pythonLicense -Description "Staged CPython license" `
    -ExpectedSizeBytes ([Int64]$sourceLock.python.license.sizeBytes) `
    -ExpectedSha256 ([string]$sourceLock.python.license.sha256) | Out-Null
$edgeModule = Join-Path $runtimeRoot 'Lib\site-packages\edge_tts\__main__.py'
Assert-CompleteFile -Path $edgeModule -Description "Staged Edge TTS module launcher" | Out-Null
$runtimeRequirements = @((@($wheelManifest.files) + @($edgeWheelManifest.files)) | ForEach-Object {
    [pscustomobject]@{
        Name = [string]$_.distribution
        NormalizedName = ([string]$_.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        Version = [string]$_.version
    }
})
Assert-CompletePythonRuntimeLicenseInventory -RuntimeRoot $runtimeRoot `
    -PinnedRequirements $runtimeRequirements -WheelManifests @($wheelManifest, $edgeWheelManifest)
$edgeRuntimeInventoryPath = Join-Path $stage 'Support\edge-tts-runtime.inventory.json'
Assert-EdgeTtsRuntimeInventory -RuntimeRoot $runtimeRoot -RequirementsPath $edgeRequirementsPath `
    -WheelManifestPath $edgeWheelManifestPath -PinnedRequirements $edgePinnedRequirements `
    -WheelManifest $edgeWheelManifest -PythonSourceLock $sourceLock.python -InventoryPath $edgeRuntimeInventoryPath
$sitePackagesLauncherDirectory = Join-Path $runtimeRoot 'Lib\site-packages\bin'
$pipDirectory = Join-Path $runtimeRoot 'Lib\site-packages\pip'
$scriptsDirectory = Join-Path $runtimeRoot 'Scripts'
$forbiddenRuntimeItems = @(Get-ChildItem -LiteralPath $runtimeRoot -Recurse -Force | Where-Object {
    ($_.PSIsContainer -and $_.Name -ceq '__pycache__') -or
    (-not $_.PSIsContainer -and $_.Extension -in @('.pyc', '.pyo'))
})
foreach ($forbiddenDirectory in @($sitePackagesLauncherDirectory, $pipDirectory, $scriptsDirectory)) {
    if (Test-Path -LiteralPath $forbiddenDirectory) {
        $forbiddenRuntimeItems += Get-Item -LiteralPath $forbiddenDirectory
    }
}
if ($forbiddenRuntimeItems.Count -gt 0) {
    throw "Staged embedded Python runtime contains generated bytecode or path-bound launchers: $($forbiddenRuntimeItems.FullName -join ', ')"
}

if ($SkipRuntimeHealth) {
    Write-Warning "Runtime import/model/FFmpeg health checks were explicitly skipped. The staged file inventory and installer header checks still ran."
}
else {
    $oldNoByteCode = $env:PYTHONDONTWRITEBYTECODE
    $healthRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("speak-complete-verify-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $healthRoot -Force | Out-Null
    $env:PYTHONDONTWRITEBYTECODE = '1'
    try {
        $runtimeCode = @'
import importlib.metadata as metadata
import json
from pathlib import Path
import sys
import torch
import whisper
import edge_tts

expected = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
actual = {name: metadata.version(name) for name in expected}
if actual != expected:
    raise SystemExit(json.dumps({"expected": expected, "actual": actual}, sort_keys=True))
if torch.cuda.is_available() or torch.version.cuda is not None:
    raise SystemExit("CPU-only torch runtime check failed")
print(json.dumps({"python": ".".join(map(str, sys.version_info[:3])), "torch": torch.__version__, "whisper": metadata.version("openai-whisper")}, sort_keys=True))
'@
        $runtimeScriptPath = Join-Path $healthRoot 'runtime-check.py'
        $runtimeExpectedPath = Join-Path $healthRoot 'runtime-expected.json'
        [System.IO.File]::WriteAllText($runtimeScriptPath, $runtimeCode, [System.Text.UTF8Encoding]::new($false))
        $runtimeExpected = @{
            'torch' = [string]$componentManifest.runtime.torch
            'openai-whisper' = [string]$componentManifest.runtime.whisper
        }
        foreach ($edgeWheel in @($edgeWheelManifest.files)) {
            $normalizedEdgeName = ([string]$edgeWheel.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
            $runtimeExpected[$normalizedEdgeName] = [string]$edgeWheel.version
        }
        [System.IO.File]::WriteAllText($runtimeExpectedPath, ($runtimeExpected | ConvertTo-Json -Compress), [System.Text.UTF8Encoding]::new($false))
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $runtimeOutput = @(& $python $runtimeScriptPath $runtimeExpectedPath 2>&1)
            $runtimeExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($runtimeExitCode -ne 0) {
            throw "Embedded runtime import/version health check failed: $($runtimeOutput -join [Environment]::NewLine)"
        }
        if (($runtimeOutput -join "`n") -notmatch '"python":\s*"3\.14\.6"') {
            throw "Embedded runtime did not report Python 3.14.6: $($runtimeOutput -join ' ')"
        }
        try {
            $ErrorActionPreference = 'Continue'
            $edgeVersionOutput = @(& $python -B -I -m edge_tts --version 2>&1)
            $edgeVersionExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($edgeVersionExitCode -ne 0 -or (($edgeVersionOutput -join "`n").Trim() -cne 'edge-tts 7.2.8')) {
            throw "Embedded Edge TTS module launch health check failed: $($edgeVersionOutput -join [Environment]::NewLine)"
        }

        $modelCode = @'
import pathlib
import sys
import torch
import whisper

model_root = pathlib.Path(sys.argv[1])
model = whisper.load_model("base", download_root=str(model_root), device="cpu", in_memory=False)
if next(model.parameters()).device.type != "cpu":
    raise SystemExit("Whisper base model did not load on CPU")
print("whisper-base CPU model health OK")
del model
'@
        $modelScriptPath = Join-Path $healthRoot 'model-check.py'
        [System.IO.File]::WriteAllText($modelScriptPath, $modelCode, [System.Text.UTF8Encoding]::new($false))
        try {
            $ErrorActionPreference = 'Continue'
            $modelOutput = @(& $python $modelScriptPath (Split-Path -Parent $modelPath) 2>&1)
            $modelExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($modelExitCode -ne 0) {
            throw "Whisper base model health check failed: $($modelOutput -join [Environment]::NewLine)"
        }

        Assert-StagedFfmpegVersion -BinRoot $ffmpegRoot -ExpectedVersion ([string]$sourceLock.ffmpeg.version)
    }
    finally {
        $env:PYTHONDONTWRITEBYTECODE = $oldNoByteCode
        if (Test-Path -LiteralPath $healthRoot) {
            Remove-Item -LiteralPath $healthRoot -Recurse -Force
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($InstallerPath)) {
    $installer = (Assert-CompleteFile -Path $InstallerPath -Description "Complete offline installer").FullName
    $expectedInstallerName = "Speak-$expectedVersion-Complete-Offline-Setup.exe"
    if ((Split-Path -Leaf $installer) -cne $expectedInstallerName) {
        throw "Complete offline installer filename must be $expectedInstallerName."
    }
    Assert-CompleteInstallerHeader -Path $installer -ExpectedVersion $expectedVersion

    $artifactDirectory = Split-Path -Parent $installer
    $slices = @(Get-ChildItem -LiteralPath $artifactDirectory -File | Where-Object {
        $_.Extension -ceq '.bin' -or $_.Name -match '(?i)\.bin\d+$'
    })
    if ($slices.Count -gt 0) {
        throw "Complete installer must be one physical setup EXE; external Inno Setup disk slices are forbidden: $($slices.Name -join ', ')"
    }
}

Write-Host "Complete installer verification passed: $stage"
