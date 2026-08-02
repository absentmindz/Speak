[CmdletBinding()]
param(
    [string]$DotnetPath = "dotnet",
    [string]$BuildPythonPath = "python",
    [string]$IsccPath = "",
    [Parameter(Mandatory = $true)]
    [string]$SourceCacheRoot,
    [string]$AppPublishRoot = "",
    [string]$PythonArchivePath = "",
    [string]$PythonSigstoreBundlePath = "",
    [string]$SigstorePath = "",
    [string]$WhisperWheelCache = "",
    [string]$WhisperWheelManifestPath = "",
    [string]$EdgeTtsWheelCache = "",
    [string]$EdgeTtsWheelManifestPath = "",
    [string]$WhisperBaseModelPath = "",
    [string]$FfmpegArchivePath = "",
    [string]$VcRedistPath = "",
    [string]$StageRoot = "",
    [string]$ArtifactsRoot = "",
    [switch]$NoRestore,
    [switch]$SkipRuntimeHealth,
    [switch]$TestMode
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "complete\CompleteInstaller.Common.ps1")
. (Join-Path $PSScriptRoot "EdgeTtsRuntime.Common.ps1")

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$completeRoot = Join-Path $PSScriptRoot "complete"
$stageParent = Join-Path $PSScriptRoot "stage"
$artifactParent = Join-Path $PSScriptRoot "artifacts"
$sourceLockPath = Join-Path $completeRoot "sources.lock.json"
$requirementsPath = Join-Path $repoRoot "tools\requirements-whisper-cpu.lock.txt"
$edgeRequirementsPath = Join-Path $repoRoot "tools\requirements-edge-tts.lock.txt"
$trackedWhisperWheelManifestPath = Join-Path $completeRoot "whisper-cpu-wheelhouse.manifest.json"
$trackedEdgeWheelManifestPath = Join-Path $PSScriptRoot "edge-tts-wheelhouse.manifest.json"
$completeSettingsPath = Join-Path $completeRoot "appsettings.complete.json"
$verifyScriptPath = Join-Path $PSScriptRoot "verify-complete-installer.ps1"
$verifyPublishPath = Join-Path $PSScriptRoot "verify-publish.ps1"

function Resolve-CompleteIscc {
    if (-not [string]::IsNullOrWhiteSpace($IsccPath)) {
        Assert-CompleteFile -Path $IsccPath -Description "Inno Setup compiler" | Out-Null
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
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw "Inno Setup was not found. Install the pinned CI compiler or pass -IsccPath."
}

function Get-CompleteAppVersion {
    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw
    $version = [string]$buildProperties.Project.PropertyGroup.VersionPrefix
    if (-not $version -or $version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
        throw "Directory.Build.props does not define a valid VersionPrefix."
    }
    return $version
}

function Resolve-CompleteCachePath {
    param(
        [string]$Value,
        [string]$DefaultLeaf,
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

function Get-CompletePinnedRequirements {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    Assert-CompleteFile -Path $Path -Description "Whisper CPU requirement lock" | Out-Null
    $items = [System.Collections.Generic.List[object]]::new()
    $seen = @{}
    foreach ($rawLine in @(Get-Content -LiteralPath $Path)) {
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
            continue
        }
        if ($line.StartsWith('-')) {
            throw "The offline requirement lock must not contain indexes, includes, editable installs, or other resolver directives: $line"
        }
        if ($line -notmatch '^(?<name>[A-Za-z0-9._-]+)==(?<version>[A-Za-z0-9!+._-]+)$') {
            throw "The offline requirement lock must use exact package pins only: $line"
        }

        $normalizedName = $Matches.name.ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        if ($seen.ContainsKey($normalizedName)) {
            throw "The offline requirement lock has a duplicate distribution: $($Matches.name)"
        }
        $seen[$normalizedName] = $true
        $items.Add([pscustomobject]@{
            Name = $Matches.name
            NormalizedName = $normalizedName
            Version = $Matches.version
        })
    }

    $required = @(
        'certifi', 'charset-normalizer', 'colorama', 'filelock', 'fsspec', 'idna',
        'jinja2', 'llvmlite', 'markupsafe', 'more-itertools', 'mpmath', 'networkx',
        'numba', 'numpy', 'openai-whisper', 'packaging', 'regex', 'requests',
        'setuptools', 'sympy', 'tiktoken', 'torch', 'tqdm', 'typing-extensions', 'urllib3'
    )
    foreach ($name in $required) {
        if (-not $seen.ContainsKey($name)) {
            throw "The offline requirement lock is missing required distribution $name."
        }
    }

    $torch = @($items | Where-Object { $_.NormalizedName -ceq 'torch' })
    if ($torch.Count -ne 1 -or $torch[0].Version -notmatch '\+cpu$') {
        throw "The complete installer baseline requires an explicitly CPU-only torch pin (torch==...+cpu)."
    }

    return @($items)
}

function Assert-CompleteWheelhouse {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WheelRoot,
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,
        [Parameter(Mandatory = $true)]
        [object[]]$PinnedRequirements
    )

    $wheelDirectory = Assert-CompleteDirectory -Path $WheelRoot -Description "Whisper CPU wheel cache"
    $manifest = Read-CompleteJson -Path $ManifestPath -Description "Whisper CPU wheel cache manifest"
    if ([int]$manifest.schemaVersion -ne 1 -or [string]$manifest.pythonVersion -cne '3.14.6') {
        throw "The wheel cache manifest must declare schemaVersion 1 and Python 3.14.6."
    }

    $requirementsHash = Get-CompleteSha256 -Path $requirementsPath
    if ([string]$manifest.requirementsSha256 -cne $requirementsHash) {
        throw "The wheel cache manifest does not bind to the current pinned requirement lock."
    }

    $manifestFiles = @($manifest.files)
    if ($manifestFiles.Count -eq 0) {
        throw "The wheel cache manifest must list every offline wheel."
    }

    $requirementsByName = @{}
    foreach ($requirement in $PinnedRequirements) {
        $requirementsByName[$requirement.NormalizedName] = $requirement
    }

    $manifestNames = @{}
    foreach ($file in $manifestFiles) {
        foreach ($property in @('relativePath', 'distribution', 'version', 'sourceUrl', 'sizeBytes', 'sha256', 'licenseDeclared')) {
            if ($null -eq $file.PSObject.Properties[$property] -or [string]::IsNullOrWhiteSpace([string]$file.$property)) {
                throw "Wheel cache manifest has an incomplete entry. Missing $property."
            }
        }
        if ([string]$file.licenseDeclared -ceq 'NOASSERTION' -or
            [string]$file.licenseDeclared -notmatch '^[A-Za-z0-9.+() -]+$') {
            throw "Wheel cache manifest has an invalid or unaudited declared license for $($file.distribution): $($file.licenseDeclared)"
        }

        $relative = ([string]$file.relativePath).Replace('\', '/')
        Assert-CompleteSafeRelativePath -RelativePath $relative -Description "Wheel cache manifest"
        if ($relative -notmatch '\.whl$') {
            throw "The wheel cache must contain only pre-built wheels: $relative"
        }
        if ($relative -match '[\\/]') {
            throw "The wheel cache must be flat and cannot contain subdirectories: $relative"
        }

        $normalizedName = ([string]$file.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        if ($manifestNames.ContainsKey($normalizedName)) {
            throw "Wheel cache manifest has more than one artifact for distribution $($file.distribution)."
        }
        $manifestNames[$normalizedName] = $file

        if (-not $requirementsByName.ContainsKey($normalizedName)) {
            throw "Wheel cache manifest contains a distribution not in tools/requirements-whisper-cpu.lock.txt: $($file.distribution)"
        }
        if ([string]$requirementsByName[$normalizedName].Version -cne [string]$file.version) {
            throw "Wheel cache manifest version mismatch for $($file.distribution)."
        }

        $normalizedFileStem = ([string]$file.relativePath).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        $expectedStem = ($normalizedName + '-' + ([string]$file.version).ToLowerInvariant().Replace('_', '-').Replace('.', '-'))
        if (-not $normalizedFileStem.StartsWith($expectedStem + '-', [System.StringComparison]::Ordinal)) {
            throw "Wheel filename does not identify the pinned distribution and version: $($file.relativePath)"
        }
    }

    foreach ($requirement in $PinnedRequirements) {
        if (-not $manifestNames.ContainsKey($requirement.NormalizedName)) {
            throw "Wheel cache manifest is missing the required wheel for $($requirement.Name)==$($requirement.Version)."
        }
    }

    Assert-CompleteExpectedFileSet -RootPath $wheelDirectory.FullName -ExpectedFiles $manifestFiles -Description "Whisper CPU wheel cache"
    return $manifest
}

function Expand-CompleteSafeZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ZipPath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,
        [Parameter(Mandatory = $true)]
        [string]$Description,
        [Int64]$MaximumExpandedBytes = 2147483648
    )

    Assert-CompleteFile -Path $ZipPath -Description $Description | Out-Null
    if (Test-Path -LiteralPath $DestinationPath) {
        throw "Refusing to extract $Description into an existing directory: $DestinationPath"
    }
    New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    $destinationFull = (Get-CompleteFullPath -Path $DestinationPath).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    [Int64]$expandedBytes = 0
    $seen = @{}
    try {
        foreach ($entry in $archive.Entries) {
            $entryName = ([string]$entry.FullName).Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($entryName)) {
                throw "$Description contains an empty archive entry."
            }
            if ($entryName.EndsWith('/')) {
                continue
            }
            Assert-CompleteSafeRelativePath -RelativePath $entryName -Description "$Description archive entry"
            if ($seen.ContainsKey($entryName)) {
                throw "$Description contains a duplicate archive entry: $entryName"
            }
            $seen[$entryName] = $true

            $expandedBytes += [Int64]$entry.Length
            if ($expandedBytes -gt $MaximumExpandedBytes) {
                throw "$Description exceeds the maximum permitted expanded size."
            }

            $target = [System.IO.Path]::GetFullPath((Join-Path $DestinationPath ($entryName.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
            if (-not $target.StartsWith($destinationFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "$Description contains a path that escapes its extraction root: $entryName"
            }
            $targetDirectory = Split-Path -Parent $target
            New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
            $input = $entry.Open()
            $output = [System.IO.File]::Open($target, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    Assert-CompleteTreeNoReparsePoints -RootPath $DestinationPath -Description "$Description extraction"
}

function Initialize-CompleteEmbeddedPython {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PythonArchive,
        [Parameter(Mandatory = $true)]
        [string]$RuntimeRoot,
        [Parameter(Mandatory = $true)]
        [string]$WheelRoot,
        [Parameter(Mandatory = $true)]
        [string]$BuildPython,
        [Parameter(Mandatory = $true)]
        [object[]]$PinnedRequirements,
        [string]$AdditionalWheelRoot = "",
        [string]$AdditionalRequirementsPath = "",
        [object[]]$AdditionalPinnedRequirements = @()
    )

    Expand-CompleteSafeZip -ZipPath $PythonArchive -DestinationPath $RuntimeRoot -Description "Python 3.14.6 embedded runtime" -MaximumExpandedBytes 1073741824
    $python = Join-Path $RuntimeRoot "python.exe"
    Assert-CompleteFile -Path $python -Description "Embedded Python executable" | Out-Null

    $pthFiles = @(Get-ChildItem -LiteralPath $RuntimeRoot -Filter "python*._pth" -File -Force)
    if ($pthFiles.Count -ne 1) {
        throw "Python 3.14.6 embedded runtime must contain exactly one python*._pth file."
    }
    $pthPath = $pthFiles[0].FullName
    $pthContent = @(
        "python314.zip",
        ".",
        "Lib\site-packages",
        "import site"
    )
    [System.IO.File]::WriteAllLines($pthPath, $pthContent, [System.Text.ASCIIEncoding]::new())

    $sitePackages = Join-Path $RuntimeRoot "Lib\site-packages"
    New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null

    $previousNoByteCode = $env:PYTHONDONTWRITEBYTECODE
    $env:PYTHONDONTWRITEBYTECODE = '1'
    try {
        $versionOutput = @(& $python -c "import sys; print('.'.join(map(str, sys.version_info[:3])))" 2>&1)
        if ($LASTEXITCODE -ne 0 -or (($versionOutput -join "`n").Trim() -cne '3.14.6')) {
            throw "The embedded Python runtime did not report exactly Python 3.14.6. Output: $($versionOutput -join ' ')"
        }

        $pipArguments = @(
            '-m', 'pip', '--isolated', 'install', '--disable-pip-version-check', '--ignore-installed',
            '--no-index', '--no-deps', '--no-compile', '--no-warn-script-location',
            '--find-links', $WheelRoot
        )
        if (-not [string]::IsNullOrWhiteSpace($AdditionalWheelRoot)) {
            $pipArguments += @('--find-links', $AdditionalWheelRoot)
        }
        $pipArguments += @('--target', $sitePackages, '-r', $requirementsPath)
        if (-not [string]::IsNullOrWhiteSpace($AdditionalRequirementsPath)) {
            $pipArguments += @('-r', $AdditionalRequirementsPath)
        }
        $installOutput = @(& $BuildPython @pipArguments 2>&1)
        $installExitCode = $LASTEXITCODE
        $installOutput | ForEach-Object { Write-Host $_ }
        if ($installExitCode -ne 0) {
            throw "Offline installation of the pinned Whisper and Edge TTS wheel sets failed."
        }

        $launcherDirectory = Join-Path $sitePackages "bin"
        if (Test-Path -LiteralPath $launcherDirectory) {
            Remove-Item -LiteralPath $launcherDirectory -Recurse -Force
        }
        Get-ChildItem -LiteralPath $RuntimeRoot -Directory -Recurse -Filter "__pycache__" |
            Sort-Object FullName -Descending |
            Remove-Item -Recurse -Force
        Get-ChildItem -LiteralPath $RuntimeRoot -File -Recurse |
            Where-Object { $_.Extension -in @('.pyc', '.pyo') } |
            Remove-Item -Force

        $expectedVersions = @{}
        foreach ($requirement in @($PinnedRequirements) + @($AdditionalPinnedRequirements)) {
            if ($expectedVersions.ContainsKey($requirement.NormalizedName) -and
                [string]$expectedVersions[$requirement.NormalizedName] -cne [string]$requirement.Version) {
                throw "The combined embedded runtime has conflicting pins for $($requirement.Name)."
            }
            $expectedVersions[$requirement.NormalizedName] = $requirement.Version
        }
        $checkCode = @'
import importlib.metadata as metadata
import json
from pathlib import Path
import sys
import torch
import whisper

expected = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
if "edge-tts" in expected:
    import edge_tts
actual = {name: metadata.version(name) for name in expected}
if actual != expected:
    raise SystemExit("installed distribution versions differ: " + json.dumps({"expected": expected, "actual": actual}, sort_keys=True))
if torch.cuda.is_available() or torch.version.cuda is not None:
    raise SystemExit("the complete installer must contain a CPU-only torch runtime")
print(json.dumps({"python": ".".join(map(str, sys.version_info[:3])), "torch": torch.__version__, "whisper": metadata.version("openai-whisper"), "edgeTts": metadata.version("edge-tts") if "edge-tts" in expected else None}, sort_keys=True))
'@
        $runtimeCheckScript = Join-Path $RuntimeRoot ".speak-runtime-check.py"
        $runtimeExpectedJson = Join-Path $RuntimeRoot ".speak-runtime-expected.json"
        [System.IO.File]::WriteAllText($runtimeCheckScript, $checkCode, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($runtimeExpectedJson, ($expectedVersions | ConvertTo-Json -Compress), [System.Text.UTF8Encoding]::new($false))
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $runtimeCheck = @(& $python $runtimeCheckScript $runtimeExpectedJson 2>&1)
            $runtimeCheckExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
            Remove-Item -LiteralPath $runtimeCheckScript, $runtimeExpectedJson -Force -ErrorAction SilentlyContinue
        }
        if ($runtimeCheckExitCode -ne 0) {
            throw "Embedded Python import/version check failed: $($runtimeCheck -join [Environment]::NewLine)"
        }
    }
    finally {
        $env:PYTHONDONTWRITEBYTECODE = $previousNoByteCode
    }

    Assert-CompleteTreeNoReparsePoints -RootPath $RuntimeRoot -Description "Installed Python runtime"
    return $python
}

function Get-CompleteTreeManifestEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $root = Assert-CompleteDirectory -Path $RootPath -Description $Description
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($file in @(Get-CompleteTreeFiles -RootPath $root.FullName -Description $Description)) {
        $entries.Add([pscustomobject]@{
            relativePath = Get-CompleteRelativePath -BasePath $root.FullName -TargetPath $file.FullName
            sizeBytes = [Int64]$file.Length
            sha256 = Get-CompleteSha256 -Path $file.FullName
        })
    }
    return @($entries | Sort-Object relativePath)
}

function Assert-CompleteVcRedistVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    $versionInfo = (Get-Item -LiteralPath $Path).VersionInfo
    $actualVersion = ([string]$versionInfo.ProductVersion).Trim()
    if ($actualVersion -cne $ExpectedVersion) {
        throw "Microsoft Visual C++ Redistributable version mismatch. Expected $ExpectedVersion; found $actualVersion."
    }
}

function Assert-CompleteFfmpegVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BinRoot,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    $ffmpeg = Join-Path $BinRoot 'ffmpeg.exe'
    $output = @(& $ffmpeg -hide_banner -version 2>&1)
    if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) {
        throw "Audited FFmpeg executable/DLL payload could not execute: $($output -join [Environment]::NewLine)"
    }
    $firstLine = ([string]$output[0]).Trim()
    if ($firstLine -notmatch ('^ffmpeg version ' + [regex]::Escape($ExpectedVersion) + '(?:\s|$)')) {
        throw "FFmpeg version mismatch. Expected $ExpectedVersion; found '$firstLine'."
    }
}

function Assert-CompleteSelfContainedPublish {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishRoot
    )

    Assert-CompleteDirectory -Path $PublishRoot -Description "Self-contained Speak publish" | Out-Null
    Assert-CompleteTreeNoReparsePoints -RootPath $PublishRoot -Description "Self-contained Speak publish"
    foreach ($file in @('Speak.exe', 'Speak.deps.json', 'Speak.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
        Assert-CompleteFile -Path (Join-Path $PublishRoot $file) -Description "Self-contained Speak publish file $file" | Out-Null
    }
    & $verifyPublishPath -PublishRoot $PublishRoot -SkipLaunch
    if ($LASTEXITCODE -ne 0) {
        throw "The self-contained Speak publish verification failed."
    }
}

function Assert-CompleteNoBuildPathLeaks {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [string[]]$ForbiddenPaths
    )

    $textExtensions = @(
        '.bat', '.cfg', '.cmd', '.conf', '.csv', '.html', '.ini', '.js',
        '.json', '.md', '.ps1', '.pth', '.py', '.pyi', '.rst', '.toml',
        '.txt', '.xml', '.yaml', '.yml'
    )
    $textNames = @('INSTALLER', 'METADATA', 'RECORD', 'REQUESTED', 'WHEEL')
    $needles = @($ForbiddenPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
        $full = [System.IO.Path]::GetFullPath($_).TrimEnd('\', '/')
        $full
        $full.Replace('\', '/')
    } | Select-Object -Unique)

    foreach ($file in @(Get-CompleteTreeFiles -RootPath $RootPath -Description "Complete installer privacy scan")) {
        if ($file.Length -gt 8388608 -or
            ($textExtensions -notcontains $file.Extension.ToLowerInvariant() -and $textNames -notcontains $file.Name)) {
            continue
        }

        $content = [System.IO.File]::ReadAllText($file.FullName)
        foreach ($needle in $needles) {
            if ($content.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $relative = Get-CompleteRelativePath -BasePath $RootPath -TargetPath $file.FullName
                throw "Complete installer privacy scan found build-machine path '$needle' in $relative."
            }
        }
    }
}

$sourceCache = Assert-CompleteDirectory -Path $SourceCacheRoot -Description "Complete-installer source cache"
Assert-CompleteFile -Path $sourceLockPath -Description "Complete-installer source lock" | Out-Null
Assert-CompleteFile -Path $requirementsPath -Description "Whisper CPU requirement lock" | Out-Null
Assert-CompleteFile -Path $edgeRequirementsPath -Description "Edge TTS requirement lock" | Out-Null
Assert-CompleteFile -Path $completeSettingsPath -Description "Complete installer appsettings" | Out-Null
Assert-CompleteFile -Path $verifyScriptPath -Description "Complete installer verifier" | Out-Null

$sourceLock = Read-CompleteJson -Path $sourceLockPath -Description "Complete-installer source lock"
if ([int]$sourceLock.schemaVersion -ne 1 -or [string]$sourceLock.python.version -cne '3.14.6' -or
    [string]$sourceLock.python.license.relativePath -cne 'LICENSE.txt' -or
    [string]$sourceLock.edgeTts.version -cne '7.2.8' -or [string]$sourceLock.whisperBase.modelId -cne 'whisper-base') {
    throw "The complete-installer source lock has an unsupported schema or baseline."
}

$version = Get-CompleteAppVersion
$iscc = Resolve-CompleteIscc
$dotnet = Get-Command $DotnetPath -ErrorAction Stop
$buildPython = Get-Command $BuildPythonPath -ErrorAction Stop
$buildPythonVersion = @(& $buildPython.Source -c "import sys; print('.'.join(map(str, sys.version_info[:3])))" 2>&1)
if ($LASTEXITCODE -ne 0 -or (($buildPythonVersion -join "`n").Trim() -cne '3.14.6')) {
    throw "The complete installer wheel staging step requires build Python 3.14.6."
}
$pythonArchive = Resolve-CompleteCachePath -Value $PythonArchivePath -DefaultLeaf ([string]$sourceLock.python.fileName) -Description "Python 3.14.6 archive"
$pythonBundle = Resolve-CompleteCachePath -Value $PythonSigstoreBundlePath -DefaultLeaf ([string]$sourceLock.python.sigstore.fileName) -Description "Python 3.14.6 Sigstore bundle"
$baseModel = Resolve-CompleteCachePath -Value $WhisperBaseModelPath -DefaultLeaf ([string]$sourceLock.whisperBase.fileName) -Description "OpenAI Whisper base checkpoint"
$vcRedist = Resolve-CompleteCachePath -Value $VcRedistPath -DefaultLeaf ([string]$sourceLock.vcRedist.fileName) -Description "Microsoft Visual C++ Redistributable"
$ffmpegArchive = Resolve-CompleteCachePath -Value $FfmpegArchivePath -DefaultLeaf ([string]$sourceLock.ffmpeg.archive.fileName) -Description "Audited FFmpeg archive"
$wheelCache = if ([string]::IsNullOrWhiteSpace($WhisperWheelCache)) { Join-Path $sourceCache.FullName "whisper-cpu-wheelhouse" } else { $WhisperWheelCache }
$wheelManifest = if ([string]::IsNullOrWhiteSpace($WhisperWheelManifestPath)) { Join-Path $sourceCache.FullName "whisper-cpu-wheelhouse.manifest.json" } else { $WhisperWheelManifestPath }
$wheelManifest = (Assert-CompleteFile -Path $wheelManifest -Description "Whisper CPU wheel cache manifest").FullName
if ((Get-CompleteSha256 -Path $wheelManifest) -cne (Get-CompleteSha256 -Path $trackedWhisperWheelManifestPath)) {
    throw "The cached Whisper CPU wheel manifest differs from the tracked audited manifest."
}
$edgeWheelCache = if ([string]::IsNullOrWhiteSpace($EdgeTtsWheelCache)) { Join-Path $sourceCache.FullName "edge-tts-wheelhouse" } else { $EdgeTtsWheelCache }
$edgeWheelManifest = if ([string]::IsNullOrWhiteSpace($EdgeTtsWheelManifestPath)) { Join-Path $sourceCache.FullName "edge-tts-wheelhouse.manifest.json" } else { $EdgeTtsWheelManifestPath }
$edgeWheelManifest = (Assert-CompleteFile -Path $edgeWheelManifest -Description "Edge TTS wheel cache manifest").FullName
if ((Get-CompleteSha256 -Path $edgeWheelManifest) -cne (Get-CompleteSha256 -Path $trackedEdgeWheelManifestPath)) {
    throw "The cached Edge TTS wheel manifest differs from the tracked audited manifest."
}

if ([string]::IsNullOrWhiteSpace($SigstorePath)) {
    throw "A sigstore verifier is required to validate the signed Python 3.14.6 source. Pass -SigstorePath."
}
$sigstore = (Assert-CompleteFile -Path $SigstorePath -Description "sigstore verifier").FullName

Assert-CompleteExactFile -Path $pythonArchive -Description "Python 3.14.6 archive" `
    -ExpectedSizeBytes ([Int64]$sourceLock.python.sizeBytes) -ExpectedSha256 ([string]$sourceLock.python.sha256) | Out-Null
Assert-CompleteExactFile -Path $pythonBundle -Description "Python 3.14.6 Sigstore bundle" `
    -ExpectedSizeBytes ([Int64]$sourceLock.python.sigstore.sizeBytes) -ExpectedSha256 ([string]$sourceLock.python.sigstore.sha256) | Out-Null
Invoke-CompleteSigstoreVerification -SigstorePath $sigstore -ArtifactPath $pythonArchive -BundlePath $pythonBundle `
    -Identity ([string]$sourceLock.python.sigstore.identity) -OidcIssuer ([string]$sourceLock.python.sigstore.oidcIssuer)
Assert-CompleteExactFile -Path $baseModel -Description "OpenAI Whisper base checkpoint" `
    -ExpectedSizeBytes ([Int64]$sourceLock.whisperBase.sizeBytes) -ExpectedSha256 ([string]$sourceLock.whisperBase.sha256) | Out-Null
Assert-CompleteExactFile -Path $vcRedist -Description "Microsoft Visual C++ Redistributable" `
    -ExpectedSizeBytes ([Int64]$sourceLock.vcRedist.sizeBytes) -ExpectedSha256 ([string]$sourceLock.vcRedist.sha256) | Out-Null
Assert-CompleteAuthenticodeSignature -Path $vcRedist -Description "Microsoft Visual C++ Redistributable" `
    -SubjectRegex ([string]$sourceLock.vcRedist.signature.subjectRegex) -IssuerRegex ([string]$sourceLock.vcRedist.signature.issuerRegex)
Assert-CompleteVcRedistVersion -Path $vcRedist -ExpectedVersion ([string]$sourceLock.vcRedist.version)
Assert-CompleteExactFile -Path $ffmpegArchive -Description "Audited FFmpeg archive" `
    -ExpectedSizeBytes ([Int64]$sourceLock.ffmpeg.archive.sizeBytes) -ExpectedSha256 ([string]$sourceLock.ffmpeg.archive.sha256) | Out-Null

$pinnedRequirements = Get-CompletePinnedRequirements -Path $requirementsPath
$wheelManifestObject = Assert-CompleteWheelhouse -WheelRoot $wheelCache -ManifestPath $wheelManifest -PinnedRequirements $pinnedRequirements
$edgePinnedRequirements = Get-EdgeTtsPinnedRequirements -Path $edgeRequirementsPath
$edgeWheelManifestObject = Assert-EdgeTtsWheelhouse -WheelRoot $edgeWheelCache -ManifestPath $edgeWheelManifest `
    -RequirementsPath $edgeRequirementsPath -PinnedRequirements $edgePinnedRequirements

$stageLeaf = if ($TestMode) { "complete-test" } else { "complete" }
$workLeaf = if ($TestMode) { "complete-test-work" } else { "complete-work" }
$artifactLeaf = if ($TestMode) { "complete-test" } else { "complete" }
$stage = if ([string]::IsNullOrWhiteSpace($StageRoot)) { Join-Path $stageParent $stageLeaf } else { $StageRoot }
$artifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) { Join-Path $artifactParent $artifactLeaf } else { $ArtifactsRoot }
$stage = Reset-CompleteGeneratedDirectory -Path $stage -ExpectedParent $stageParent -ExpectedLeaf $stageLeaf
$workRoot = Reset-CompleteGeneratedDirectory -Path (Join-Path $stageParent $workLeaf) -ExpectedParent $stageParent -ExpectedLeaf $workLeaf
$artifacts = Reset-CompleteGeneratedDirectory -Path $artifacts -ExpectedParent $artifactParent -ExpectedLeaf $artifactLeaf

try {
    $ffmpegExtractionRoot = Join-Path $workRoot "FfmpegArchive"
    Expand-CompleteSafeZip -ZipPath $ffmpegArchive -DestinationPath $ffmpegExtractionRoot -Description "Audited FFmpeg archive" -MaximumExpandedBytes 1073741824
    $ffmpegBinRelativePath = ([string]$sourceLock.ffmpeg.archive.binRelativePath).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    Assert-CompleteSafeRelativePath -RelativePath $ffmpegBinRelativePath -Description "FFmpeg archive bin path"
    $ffmpegBin = (Assert-CompleteDirectory -Path (Join-Path $ffmpegExtractionRoot $ffmpegBinRelativePath) -Description "Audited FFmpeg executable and DLL payload").FullName
    Assert-CompleteSelectedFiles -RootPath $ffmpegBin -ExpectedFiles @($sourceLock.ffmpeg.files) -Description "Audited FFmpeg executable and DLL payload"
    Assert-CompleteFfmpegVersion -BinRoot $ffmpegBin -ExpectedVersion ([string]$sourceLock.ffmpeg.version)

    $publishWorkRoot = Join-Path $workRoot "AppPublish"
    if ([string]::IsNullOrWhiteSpace($AppPublishRoot)) {
        if (-not $NoRestore) {
            & $dotnet.Source restore (Join-Path $repoRoot "Speak.sln") --locked-mode
            if ($LASTEXITCODE -ne 0) {
                throw "Locked package restore failed."
            }
        }

        & $dotnet.Source publish (Join-Path $repoRoot "Speak.csproj") `
            -c Release `
            --no-restore `
            --runtime win-x64 `
            --self-contained true `
            -o $publishWorkRoot `
            -p:DebugType=None `
            -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) {
            throw "Self-contained Speak publish failed."
        }
    }
    else {
        $publishWorkRoot = (Assert-CompleteDirectory -Path $AppPublishRoot -Description "Explicit self-contained Speak publish").FullName
    }

    Assert-CompleteSelfContainedPublish -PublishRoot $publishWorkRoot
    $appEntries = Get-CompleteTreeManifestEntries -RootPath $publishWorkRoot -Description "Self-contained Speak publish"
    $appStage = Join-Path $stage "App"
    Copy-CompleteExactTree -SourceRoot $publishWorkRoot -DestinationRoot $appStage -ExpectedFiles $appEntries -Description "Self-contained Speak application"

    $stagedSettings = Join-Path $appStage "appsettings.json"
    if (Test-Path -LiteralPath $stagedSettings) {
        throw "The complete installer must create its audited appsettings.json; the publish tree already contains one."
    }
    Copy-Item -LiteralPath $completeSettingsPath -Destination $stagedSettings -Force
    Assert-CompleteFile -Path $stagedSettings -Description "Complete installer appsettings" | Out-Null

    $runtimeStage = Join-Path $stage "Runtime\Whisper"
    New-Item -ItemType Directory -Path (Split-Path -Parent $runtimeStage) -Force | Out-Null
    $runtimePython = [string](Initialize-CompleteEmbeddedPython -PythonArchive $pythonArchive -RuntimeRoot $runtimeStage `
        -WheelRoot $wheelCache -BuildPython $buildPython.Source -PinnedRequirements $pinnedRequirements `
        -AdditionalWheelRoot $edgeWheelCache -AdditionalRequirementsPath $edgeRequirementsPath `
        -AdditionalPinnedRequirements $edgePinnedRequirements)
    Assert-CompleteFile -Path $runtimePython -Description "Combined Whisper and Edge TTS Python runtime" | Out-Null
    Assert-CompletePythonRuntimeLicenseInventory -RuntimeRoot $runtimeStage `
        -PinnedRequirements (@($pinnedRequirements) + @($edgePinnedRequirements)) `
        -WheelManifests @($wheelManifestObject, $edgeWheelManifestObject)
    Assert-CompleteExactFile -Path (Join-Path $runtimeStage 'LICENSE.txt') -Description "Staged CPython license" `
        -ExpectedSizeBytes ([Int64]$sourceLock.python.license.sizeBytes) `
        -ExpectedSha256 ([string]$sourceLock.python.license.sha256) | Out-Null

    $ffmpegStage = Join-Path $stage "Ffmpeg\bin"
    New-Item -ItemType Directory -Path (Split-Path -Parent $ffmpegStage) -Force | Out-Null
    Copy-CompleteSelectedFiles -SourceRoot $ffmpegBin -DestinationRoot $ffmpegStage -ExpectedFiles @($sourceLock.ffmpeg.files) -Description "FFmpeg executable and DLL payload"

    $modelDirectory = Join-Path $stage "Models\whisper"
    New-Item -ItemType Directory -Path $modelDirectory -Force | Out-Null
    $stagedModel = Join-Path $modelDirectory "base.pt"
    Copy-Item -LiteralPath $baseModel -Destination $stagedModel -Force
    Assert-CompleteExactFile -Path $stagedModel -Description "Staged OpenAI Whisper base checkpoint" `
        -ExpectedSizeBytes ([Int64]$sourceLock.whisperBase.sizeBytes) -ExpectedSha256 ([string]$sourceLock.whisperBase.sha256) | Out-Null

    $supportDirectory = Join-Path $stage "Support"
    New-Item -ItemType Directory -Path $supportDirectory -Force | Out-Null
    $stagedVcRedist = Join-Path $supportDirectory ([string]$sourceLock.vcRedist.fileName)
    Copy-Item -LiteralPath $vcRedist -Destination $stagedVcRedist -Force
    Assert-CompleteExactFile -Path $stagedVcRedist -Description "Staged Microsoft Visual C++ Redistributable" `
        -ExpectedSizeBytes ([Int64]$sourceLock.vcRedist.sizeBytes) -ExpectedSha256 ([string]$sourceLock.vcRedist.sha256) | Out-Null
    Assert-CompleteAuthenticodeSignature -Path $stagedVcRedist -Description "Staged Microsoft Visual C++ Redistributable" `
        -SubjectRegex ([string]$sourceLock.vcRedist.signature.subjectRegex) -IssuerRegex ([string]$sourceLock.vcRedist.signature.issuerRegex)
    Assert-CompleteVcRedistVersion -Path $stagedVcRedist -ExpectedVersion ([string]$sourceLock.vcRedist.version)
    Copy-Item -LiteralPath $requirementsPath -Destination (Join-Path $supportDirectory "requirements-whisper-cpu.lock.txt") -Force
    Copy-Item -LiteralPath $edgeRequirementsPath -Destination (Join-Path $supportDirectory "requirements-edge-tts.lock.txt") -Force
    Copy-Item -LiteralPath $sourceLockPath -Destination (Join-Path $supportDirectory "sources.lock.json") -Force
    Copy-Item -LiteralPath $wheelManifest -Destination (Join-Path $supportDirectory "whisper-cpu-wheelhouse.manifest.json") -Force
    Copy-Item -LiteralPath $edgeWheelManifest -Destination (Join-Path $supportDirectory "edge-tts-wheelhouse.manifest.json") -Force
    $stagedEdgeRequirements = Join-Path $supportDirectory 'requirements-edge-tts.lock.txt'
    $stagedEdgeManifest = Join-Path $supportDirectory 'edge-tts-wheelhouse.manifest.json'
    $stagedEdgeInventory = Join-Path $supportDirectory 'edge-tts-runtime.inventory.json'
    Write-EdgeTtsRuntimeInventory -RuntimeRoot $runtimeStage -RequirementsPath $stagedEdgeRequirements `
        -WheelManifestPath $stagedEdgeManifest -PinnedRequirements $edgePinnedRequirements `
        -WheelManifest $edgeWheelManifestObject -PythonSourceLock $sourceLock.python -InventoryPath $stagedEdgeInventory

    $sourceLockHash = Get-CompleteSha256 -Path $sourceLockPath
    $requirementsHash = Get-CompleteSha256 -Path $requirementsPath
    $edgeRequirementsHash = Get-CompleteSha256 -Path $edgeRequirementsPath
    $spdxPackages = [System.Collections.Generic.List[object]]::new()
    $spdxPackages.Add([ordered]@{
        name = 'Speak'
        SPDXID = 'SPDXRef-Package-Speak'
        versionInfo = $version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'Apache-2.0'
        copyrightText = 'NOASSERTION'
        supplier = 'NOASSERTION'
    })
    $spdxPackages.Add([ordered]@{
        name = 'CPython'
        SPDXID = 'SPDXRef-Package-CPython'
        versionInfo = [string]$sourceLock.python.version
        downloadLocation = [string]$sourceLock.python.sourceUrl
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'PSF-2.0'
        copyrightText = 'NOASSERTION'
        checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = [string]$sourceLock.python.sha256 })
        supplier = 'Organization: Python Software Foundation'
    })
    $spdxPackages.Add([ordered]@{
        name = 'FFmpeg'
        SPDXID = 'SPDXRef-Package-FFmpeg'
        versionInfo = [string]$sourceLock.ffmpeg.version
        downloadLocation = [string]$sourceLock.ffmpeg.archive.sourceUrl
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = [string]$sourceLock.ffmpeg.license
        copyrightText = 'NOASSERTION'
        checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = [string]$sourceLock.ffmpeg.archive.sha256 })
        supplier = 'NOASSERTION'
    })
    $spdxPackages.Add([ordered]@{
        name = 'OpenAI-Whisper-Base-Model'
        SPDXID = 'SPDXRef-Package-OpenAI-Whisper-Base-Model'
        versionInfo = [string]$sourceLock.whisperBase.modelId
        downloadLocation = [string]$sourceLock.whisperBase.sourceUrl
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'MIT'
        copyrightText = 'NOASSERTION'
        checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = [string]$sourceLock.whisperBase.sha256 })
        supplier = 'Organization: OpenAI'
    })
    $spdxPackages.Add([ordered]@{
        name = 'Microsoft-Visual-Cpp-Redistributable-x64'
        SPDXID = 'SPDXRef-Package-Microsoft-Visual-Cpp-Redistributable-x64'
        versionInfo = [string]$sourceLock.vcRedist.version
        downloadLocation = [string]$sourceLock.vcRedist.sourceUrl
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'LicenseRef-Microsoft-Visual-Cpp-Redistributable'
        copyrightText = 'NOASSERTION'
        checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = [string]$sourceLock.vcRedist.sha256 })
        supplier = 'Organization: Microsoft Corporation'
    })
    foreach ($wheelEntry in @($wheelManifestObject.files)) {
        $normalizedDistribution = ([string]$wheelEntry.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        $spdxId = 'SPDXRef-Package-Python-' + [regex]::Replace($normalizedDistribution, '[^A-Za-z0-9.-]', '-')
        $spdxPackages.Add([ordered]@{
            name = [string]$wheelEntry.distribution
            SPDXID = $spdxId
            versionInfo = [string]$wheelEntry.version
            downloadLocation = [string]$wheelEntry.sourceUrl
            filesAnalyzed = $false
            licenseConcluded = 'NOASSERTION'
            licenseDeclared = [string]$wheelEntry.licenseDeclared
            copyrightText = 'NOASSERTION'
            checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = [string]$wheelEntry.sha256 })
            supplier = 'NOASSERTION'
            comment = 'Checksum identifies the audited cached wheel listed in Support/whisper-cpu-wheelhouse.manifest.json.'
        })
    }
    $spdxPackagesByNormalizedName = @{}
    foreach ($package in $spdxPackages) {
        $normalizedPackageName = ([string]$package.name).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        $spdxPackagesByNormalizedName[$normalizedPackageName] = $package
    }
    foreach ($wheelEntry in @($edgeWheelManifestObject.files)) {
        $normalizedDistribution = ([string]$wheelEntry.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        if ($spdxPackagesByNormalizedName.ContainsKey($normalizedDistribution)) {
            $existingPackage = $spdxPackagesByNormalizedName[$normalizedDistribution]
            $existingSha256 = @($existingPackage.checksums | Where-Object { [string]$_.algorithm -ceq 'SHA256' })
            if ([string]$existingPackage.versionInfo -cne [string]$wheelEntry.version -or
                $existingSha256.Count -ne 1 -or
                [string]$existingSha256[0].checksumValue -cne [string]$wheelEntry.sha256 -or
                [string]$existingPackage.licenseDeclared -cne [string]$wheelEntry.licenseDeclared -or
                [string]$existingPackage.downloadLocation -cne [string]$wheelEntry.sourceUrl) {
                throw "Whisper and Edge TTS wheel manifests disagree for $($wheelEntry.distribution)."
            }
            continue
        }

        $spdxId = 'SPDXRef-Package-Python-' + [regex]::Replace($normalizedDistribution, '[^A-Za-z0-9.-]', '-')
        $package = [ordered]@{
            name = [string]$wheelEntry.distribution
            SPDXID = $spdxId
            versionInfo = [string]$wheelEntry.version
            downloadLocation = [string]$wheelEntry.sourceUrl
            filesAnalyzed = $false
            licenseConcluded = 'NOASSERTION'
            licenseDeclared = [string]$wheelEntry.licenseDeclared
            copyrightText = 'NOASSERTION'
            checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = [string]$wheelEntry.sha256 })
            supplier = 'NOASSERTION'
            comment = 'Checksum identifies the audited cached wheel listed in Support/edge-tts-wheelhouse.manifest.json.'
        }
        $spdxPackages.Add($package)
        $spdxPackagesByNormalizedName[$normalizedDistribution] = $package
    }
    $spdxRelationships = [System.Collections.Generic.List[object]]::new()
    $spdxRelationships.Add([ordered]@{
        spdxElementId = 'SPDXRef-DOCUMENT'
        relationshipType = 'DESCRIBES'
        relatedSpdxElement = 'SPDXRef-Package-Speak'
    })
    foreach ($package in $spdxPackages) {
        if ([string]$package.SPDXID -ceq 'SPDXRef-Package-Speak') {
            continue
        }
        $spdxRelationships.Add([ordered]@{
            spdxElementId = 'SPDXRef-Package-Speak'
            relationshipType = 'DEPENDS_ON'
            relatedSpdxElement = [string]$package.SPDXID
        })
    }
    $spdx = [ordered]@{
        spdxVersion = 'SPDX-2.3'
        dataLicense = 'CC0-1.0'
        SPDXID = 'SPDXRef-DOCUMENT'
        name = "Speak-$version-Complete-Offline-CPU"
        documentNamespace = "https://github.com/absentmindz/Speak/sbom/complete/$version/$sourceLockHash-$requirementsHash-$edgeRequirementsHash"
        creationInfo = [ordered]@{
            created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
            creators = @('Tool: Speak complete-installer builder')
        }
        documentDescribes = @('SPDXRef-Package-Speak')
        packages = @($spdxPackages)
        relationships = @($spdxRelationships)
        hasExtractedLicensingInfos = @([ordered]@{
            licenseId = 'LicenseRef-Microsoft-Visual-Cpp-Redistributable'
            extractedText = 'Redistribution and use are subject to the applicable Microsoft Visual C++ Redistributable license terms.'
            name = 'Microsoft Visual C++ Redistributable terms'
            seeAlsos = @('https://visualstudio.microsoft.com/license-terms/')
        })
    }
    $spdxPath = Join-Path $supportDirectory "Speak-$version-Complete-Offline.spdx.json"
    Write-CompleteJson -Value $spdx -Path $spdxPath

    $licensesDirectory = Join-Path $stage "Licenses"
    New-Item -ItemType Directory -Path $licensesDirectory -Force | Out-Null
    $licenseSources = @(
        @{ Source = Join-Path $completeRoot "Complete-THIRD-PARTY-NOTICES.txt"; Destination = "THIRD-PARTY-NOTICES.txt" },
        @{ Source = Join-Path $completeRoot "FFmpeg-NOTICE-complete.txt"; Destination = "FFmpeg-NOTICE.txt" },
        @{ Source = Join-Path $completeRoot "FFmpeg-LGPL-3.0.txt"; Destination = "FFmpeg-LGPL-3.0.txt" },
        @{ Source = Join-Path $PSScriptRoot "model-licenses\Whisper-MIT-LICENSE.txt"; Destination = "Whisper-MIT-LICENSE.txt" }
    )
    foreach ($license in $licenseSources) {
        Assert-CompleteFile -Path $license.Source -Description "Required complete-installer license file" | Out-Null
        Copy-Item -LiteralPath $license.Source -Destination (Join-Path $licensesDirectory $license.Destination) -Force
    }

    Assert-CompleteNoBuildPathLeaks -RootPath $stage -ForbiddenPaths @(
        $repoRoot,
        $sourceCache.FullName,
        (Split-Path -Parent $buildPython.Source)
    )
    Assert-CompleteTreeNoReparsePoints -RootPath $stage -Description "Complete installer staged payload"
    $componentRoots = [ordered]@{
        Application = 'App'
        PythonRuntime = 'Runtime/Whisper'
        FFmpeg = 'Ffmpeg/bin'
        WhisperBaseModel = 'Models/whisper'
        VcRedist = 'Support/VC_redist.x64.exe'
        SupportEvidence = 'Support'
        Licenses = 'Licenses'
    }
    $componentManifestPath = Join-Path $stage "component-manifest.json"
    $componentManifest = [ordered]@{
        schemaVersion = 1
        installerKind = 'complete-offline-cpu'
        appVersion = $version
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        sourceLockSha256 = Get-CompleteSha256 -Path $sourceLockPath
        runtime = [ordered]@{
            pythonVersion = [string]$sourceLock.python.version
            torch = (@($pinnedRequirements | Where-Object { $_.NormalizedName -ceq 'torch' })[0]).Version
            whisper = (@($pinnedRequirements | Where-Object { $_.NormalizedName -ceq 'openai-whisper' })[0]).Version
            edgeTts = (@($edgePinnedRequirements | Where-Object { $_.NormalizedName -ceq 'edge-tts' })[0]).Version
            modelId = [string]$sourceLock.whisperBase.modelId
            defaultDevice = 'auto'
        }
        files = @(Get-CompleteFileInventory -RootPath $stage -ExcludedRelativePaths @('component-manifest.json') -ComponentRoots $componentRoots)
    }
    Write-CompleteJson -Value $componentManifest -Path $componentManifestPath

    $verifyArguments = @{
        StageRoot = $stage
        Version = $version
        SkipRuntimeHealth = [bool]$SkipRuntimeHealth
    }
    & $verifyScriptPath @verifyArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Complete installer staged payload verification failed."
    }

    Push-Location $PSScriptRoot
    try {
        $isccArguments = @(
            "/DAppVersion=$version",
            "/DCompleteStageRoot=$stage",
            "/DCompleteArtifactsRoot=$artifacts"
        )
        if ($TestMode) {
            $isccArguments += "/DTestMode=1"
        }
        $isccArguments += "SpeakComplete.iss"
        & $iscc @isccArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Complete offline installer compilation failed."
        }
    }
    finally {
        Pop-Location
    }

    $installerPath = Join-Path $artifacts "Speak-$version-Complete-Offline-Setup.exe"
    Assert-CompleteFile -Path $installerPath -Description "Complete offline installer" | Out-Null
    $unexpectedArtifactFiles = @(Get-ChildItem -LiteralPath $artifacts -Recurse -File | Where-Object {
        $_.Name -cne (Split-Path -Leaf $installerPath)
    })
    if ($unexpectedArtifactFiles.Count -gt 0) {
        throw "The complete installer must produce one physical setup EXE and no external payload files. Found: $($unexpectedArtifactFiles.FullName -join ', ')"
    }

    $installerVerifyArguments = @{
        StageRoot = $stage
        InstallerPath = $installerPath
        Version = $version
        SkipRuntimeHealth = [bool]$SkipRuntimeHealth
    }
    & $verifyScriptPath @installerVerifyArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Complete offline installer verification failed."
    }

    Write-Host "Complete offline installer built and verified: $installerPath"
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Assert-CompleteTreeNoReparsePoints -RootPath $workRoot -Description "Complete installer temporary work directory"
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
