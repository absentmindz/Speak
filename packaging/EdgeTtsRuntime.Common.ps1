Set-StrictMode -Version Latest

function Get-EdgeTtsPinnedRequirements {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    Assert-CompleteFile -Path $Path -Description "Edge TTS requirement lock" | Out-Null
    $items = [System.Collections.Generic.List[object]]::new()
    $seen = @{}
    foreach ($rawLine in @(Get-Content -LiteralPath $Path)) {
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
            continue
        }
        if ($line.StartsWith('-')) {
            throw "The Edge TTS requirement lock must not contain indexes, includes, editable installs, or resolver directives: $line"
        }
        if ($line -notmatch '^(?<name>[A-Za-z0-9._-]+)==(?<version>[A-Za-z0-9!+._-]+)$') {
            throw "The Edge TTS requirement lock must use exact package pins only: $line"
        }

        $normalizedName = $Matches.name.ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        if ($seen.ContainsKey($normalizedName)) {
            throw "The Edge TTS requirement lock has a duplicate distribution: $($Matches.name)"
        }
        $seen[$normalizedName] = $true
        $items.Add([pscustomobject]@{
            Name = $Matches.name
            NormalizedName = $normalizedName
            Version = $Matches.version
        })
    }

    $required = @(
        'aiohappyeyeballs', 'aiohttp', 'aiosignal', 'attrs', 'certifi',
        'edge-tts', 'frozenlist', 'idna', 'multidict', 'propcache',
        'tabulate', 'typing-extensions', 'yarl'
    )
    foreach ($name in $required) {
        if (-not $seen.ContainsKey($name)) {
            throw "The Edge TTS requirement lock is missing required distribution $name."
        }
    }
    if ($seen.Count -ne $required.Count) {
        throw "The Edge TTS requirement lock contains an unexpected distribution."
    }

    $edge = @($items | Where-Object { $_.NormalizedName -ceq 'edge-tts' })
    if ($edge.Count -ne 1 -or $edge[0].Version -cne '7.2.8') {
        throw "Speak packages require the audited edge-tts==7.2.8 baseline."
    }

    return @($items)
}

function Assert-EdgeTtsWheelhouse {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WheelRoot,
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,
        [Parameter(Mandatory = $true)]
        [string]$RequirementsPath,
        [Parameter(Mandatory = $true)]
        [object[]]$PinnedRequirements
    )

    $wheelDirectory = Assert-CompleteDirectory -Path $WheelRoot -Description "Edge TTS wheel cache"
    $manifest = Read-CompleteJson -Path $ManifestPath -Description "Edge TTS wheel cache manifest"
    if ([int]$manifest.schemaVersion -ne 1 -or [string]$manifest.pythonVersion -cne '3.14.6') {
        throw "The Edge TTS wheel manifest must declare schemaVersion 1 and Python 3.14.6."
    }

    $requirementsHash = Get-CompleteSha256 -Path $RequirementsPath
    if ([string]$manifest.requirementsSha256 -cne $requirementsHash) {
        throw "The Edge TTS wheel manifest does not bind to the current requirement lock."
    }

    $requirementsByName = @{}
    foreach ($requirement in $PinnedRequirements) {
        $requirementsByName[$requirement.NormalizedName] = $requirement
    }

    $manifestFiles = @($manifest.files)
    if ($manifestFiles.Count -ne $requirementsByName.Count) {
        throw "The Edge TTS wheel manifest must list exactly one wheel per pinned distribution."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $manifestNames = @{}
    foreach ($file in $manifestFiles) {
        foreach ($property in @('relativePath', 'distribution', 'version', 'sourceUrl', 'sizeBytes', 'sha256', 'licenseDeclared', 'licenseFiles')) {
            if ($null -eq $file.PSObject.Properties[$property] -or [string]::IsNullOrWhiteSpace([string]$file.$property)) {
                throw "Edge TTS wheel manifest has an incomplete entry. Missing $property."
            }
        }

        $relative = ([string]$file.relativePath).Replace('\', '/')
        Assert-CompleteSafeRelativePath -RelativePath $relative -Description "Edge TTS wheel manifest"
        if ($relative -notmatch '\.whl$' -or $relative -match '[\\/]') {
            throw "The Edge TTS wheel cache must be a flat directory containing wheels only: $relative"
        }
        if ([string]$file.sourceUrl -notmatch '^https://files\.pythonhosted\.org/') {
            throw "Edge TTS wheel source URL must use the canonical files.pythonhosted.org host: $($file.sourceUrl)"
        }
        if ([string]$file.licenseDeclared -notmatch '^[A-Za-z0-9.+() -]+$') {
            throw "Edge TTS wheel manifest has an invalid declared license: $($file.licenseDeclared)"
        }

        $licenseEntries = @($file.licenseFiles)
        if ($licenseEntries.Count -eq 0) {
            throw "Edge TTS wheel manifest has no packaged license files for $($file.distribution)."
        }
        $seenLicensePaths = @{}
        foreach ($licenseEntry in $licenseEntries) {
            foreach ($property in @('relativePath', 'sizeBytes', 'sha256')) {
                if ($null -eq $licenseEntry.PSObject.Properties[$property] -or
                    [string]::IsNullOrWhiteSpace([string]$licenseEntry.$property)) {
                    throw "Edge TTS wheel manifest has incomplete license evidence for $($file.distribution). Missing $property."
                }
            }
            $licenseRelative = ([string]$licenseEntry.relativePath).Replace('\', '/')
            Assert-CompleteSafeRelativePath -RelativePath $licenseRelative -Description "Edge TTS wheel license manifest"
            if (-not $licenseRelative.StartsWith('licenses/', [System.StringComparison]::Ordinal) -or
                $seenLicensePaths.ContainsKey($licenseRelative)) {
                throw "Edge TTS wheel manifest has an invalid or duplicate license path for $($file.distribution): $licenseRelative"
            }
            if ([Int64]$licenseEntry.sizeBytes -le 0 -or [string]$licenseEntry.sha256 -cnotmatch '^[0-9a-f]{64}$') {
                throw "Edge TTS wheel manifest has invalid license size or SHA-256 evidence for $($file.distribution): $licenseRelative"
            }
            $seenLicensePaths[$licenseRelative] = $true
        }

        $normalizedName = ([string]$file.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        if ($manifestNames.ContainsKey($normalizedName)) {
            throw "Edge TTS wheel manifest has more than one artifact for $($file.distribution)."
        }
        $manifestNames[$normalizedName] = $file

        if (-not $requirementsByName.ContainsKey($normalizedName)) {
            throw "Edge TTS wheel manifest contains an unpinned distribution: $($file.distribution)"
        }
        if ([string]$requirementsByName[$normalizedName].Version -cne [string]$file.version) {
            throw "Edge TTS wheel manifest version mismatch for $($file.distribution)."
        }

        $normalizedFileStem = $relative.ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        $expectedStem = ($normalizedName + '-' + ([string]$file.version).ToLowerInvariant().Replace('_', '-').Replace('.', '-'))
        if (-not $normalizedFileStem.StartsWith($expectedStem + '-', [System.StringComparison]::Ordinal)) {
            throw "Edge TTS wheel filename does not identify its pinned distribution and version: $relative"
        }

        $wheelPath = Join-Path $wheelDirectory.FullName $relative
        $archive = [System.IO.Compression.ZipFile]::OpenRead($wheelPath)
        try {
            foreach ($licenseEntry in $licenseEntries) {
                $licenseRelative = ([string]$licenseEntry.relativePath).Replace('\', '/')
                $matches = @($archive.Entries | Where-Object {
                    $entryPath = ([string]$_.FullName).Replace('\', '/')
                    $entryPath -match '\.dist-info/' -and
                    $entryPath.EndsWith('/' + $licenseRelative, [System.StringComparison]::Ordinal)
                })
                if ($matches.Count -ne 1) {
                    throw "Edge TTS wheel $relative must contain exactly one $licenseRelative license entry."
                }
                $entry = $matches[0]
                if ([Int64]$entry.Length -ne [Int64]$licenseEntry.sizeBytes) {
                    throw "Edge TTS wheel license size mismatch for $($file.distribution): $licenseRelative"
                }
                $stream = $entry.Open()
                $algorithm = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $actualHash = ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
                }
                finally {
                    $algorithm.Dispose()
                    $stream.Dispose()
                }
                if ($actualHash -cne [string]$licenseEntry.sha256) {
                    throw "Edge TTS wheel license SHA-256 mismatch for $($file.distribution): $licenseRelative"
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    foreach ($requirement in $PinnedRequirements) {
        if (-not $manifestNames.ContainsKey($requirement.NormalizedName)) {
            throw "Edge TTS wheel manifest is missing $($requirement.Name)==$($requirement.Version)."
        }
    }

    Assert-CompleteExpectedFileSet -RootPath $wheelDirectory.FullName -ExpectedFiles $manifestFiles -Description "Edge TTS wheel cache"
    return $manifest
}

function Assert-EdgeTtsRuntimeLicenseInventory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeRoot,
        [Parameter(Mandatory = $true)]
        [object[]]$PinnedRequirements,
        [Parameter(Mandatory = $true)]
        [object]$WheelManifest,
        [object]$PythonSourceLock = $null,
        [switch]$AllowAdditionalDistributions
    )

    $runtime = Assert-CompleteDirectory -Path $RuntimeRoot -Description "Edge TTS runtime"
    $pythonLicense = Assert-CompleteFile -Path (Join-Path $runtime.FullName "LICENSE.txt") `
        -Description "CPython runtime license"
    if ($pythonLicense.Length -le 0) {
        throw "The bundled CPython runtime license is empty."
    }
    if ($null -ne $PythonSourceLock) {
        if ($null -eq $PythonSourceLock.PSObject.Properties['license'] -or
            [string]$PythonSourceLock.license.relativePath -cne 'LICENSE.txt') {
            throw "The Python source lock does not bind the bundled CPython license."
        }
        Assert-CompleteExactFile -Path $pythonLicense.FullName -Description "Bundled CPython license" `
            -ExpectedSizeBytes ([Int64]$PythonSourceLock.license.sizeBytes) `
            -ExpectedSha256 ([string]$PythonSourceLock.license.sha256) | Out-Null
    }

    $sitePackages = Assert-CompleteDirectory -Path (Join-Path $runtime.FullName "Lib\site-packages") `
        -Description "Edge TTS runtime site-packages"
    $manifestByName = @{}
    foreach ($entry in @($WheelManifest.files)) {
        $normalized = ([string]$entry.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        if ([string]$entry.licenseDeclared -ceq 'NOASSERTION' -or
            [string]::IsNullOrWhiteSpace([string]$entry.licenseDeclared)) {
            throw "The Edge TTS wheel manifest lacks an audited license for $($entry.distribution)."
        }
        $manifestByName[$normalized] = $entry
    }

    $installedByName = @{}
    foreach ($directory in @(Get-ChildItem -LiteralPath $sitePackages.FullName -Directory -Filter '*.dist-info' -Force)) {
        $metadataPath = Join-Path $directory.FullName 'METADATA'
        Assert-CompleteFile -Path $metadataPath -Description "Installed Python distribution metadata" | Out-Null
        $metadata = [System.IO.File]::ReadAllText($metadataPath)
        $nameMatch = [regex]::Match($metadata, '(?mi)^Name:\s*(?<value>[^\r\n]+)')
        $versionMatch = [regex]::Match($metadata, '(?mi)^Version:\s*(?<value>[^\r\n]+)')
        if (-not $nameMatch.Success -or -not $versionMatch.Success) {
            throw "Installed Python distribution metadata is missing Name or Version: $metadataPath"
        }
        $name = $nameMatch.Groups['value'].Value.Trim()
        $normalized = $name.ToLowerInvariant().Replace('_', '-').Replace('.', '-')
        if ($installedByName.ContainsKey($normalized)) {
            throw "The Edge TTS runtime contains duplicate installed metadata for $name."
        }

        $declaredLicensePaths = @([regex]::Matches($metadata, '(?mi)^License-File:\s*(?<value>[^\r\n]+)') | ForEach-Object {
            $_.Groups['value'].Value.Trim().Replace('\', '/')
        })
        if ($declaredLicensePaths.Count -eq 0) {
            throw "Installed Python distribution $name metadata does not declare its packaged license files."
        }
        $installedLicensePaths = [System.Collections.Generic.List[string]]::new()
        foreach ($declaredLicenseRelative in $declaredLicensePaths) {
            Assert-CompleteSafeRelativePath -RelativePath $declaredLicenseRelative -Description "Installed Python distribution license metadata"
            $candidates = @(
                $declaredLicenseRelative,
                ('licenses/' + $declaredLicenseRelative)
            ) | Select-Object -Unique
            $existingCandidates = @($candidates | Where-Object {
                Test-Path -LiteralPath (Join-Path $directory.FullName ($_.Replace('/', [System.IO.Path]::DirectorySeparatorChar))) -PathType Leaf
            })
            if ($existingCandidates.Count -ne 1) {
                throw "Installed Python distribution $name must contain exactly one packaged copy of declared license $declaredLicenseRelative."
            }
            $licenseRelative = [string]$existingCandidates[0]
            $licensePath = Join-Path $directory.FullName ($licenseRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            $licenseFile = Assert-CompleteFile -Path $licensePath -Description "Installed Python distribution $name license $licenseRelative"
            if ($licenseFile.Length -le 0) {
                throw "Installed Python distribution $name has an empty packaged license file: $licenseRelative"
            }
            $installedLicensePaths.Add($licenseRelative)
        }

        if ($manifestByName.ContainsKey($normalized)) {
            $expectedLicenseFiles = @($manifestByName[$normalized].licenseFiles)
            $expectedLicensePaths = @($expectedLicenseFiles | ForEach-Object { ([string]$_.relativePath).Replace('\', '/') } | Sort-Object)
            $actualLicensePaths = @($installedLicensePaths | Sort-Object)
            if (($expectedLicensePaths.Count -ne $actualLicensePaths.Count) -or
                (($expectedLicensePaths -join "`n") -cne ($actualLicensePaths -join "`n"))) {
                throw "Installed Python distribution $name license inventory differs from the audited wheel manifest."
            }
            foreach ($licenseEntry in $expectedLicenseFiles) {
                $licenseRelative = ([string]$licenseEntry.relativePath).Replace('\', '/')
                $licensePath = Join-Path $directory.FullName ($licenseRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
                Assert-CompleteExactFile -Path $licensePath -Description "Installed Python distribution $name license $licenseRelative" `
                    -ExpectedSizeBytes ([Int64]$licenseEntry.sizeBytes) -ExpectedSha256 ([string]$licenseEntry.sha256) | Out-Null
            }
        }

        $installedByName[$normalized] = [pscustomobject]@{
            Name = $name
            Version = $versionMatch.Groups['value'].Value.Trim()
            Directory = $directory.FullName
        }
    }

    foreach ($requirement in $PinnedRequirements) {
        if (-not $installedByName.ContainsKey($requirement.NormalizedName)) {
            throw "The Edge TTS runtime is missing installed metadata for $($requirement.Name)."
        }
        if ([string]$installedByName[$requirement.NormalizedName].Version -cne [string]$requirement.Version) {
            throw "The Edge TTS runtime version mismatch for $($requirement.Name)."
        }
        if (-not $manifestByName.ContainsKey($requirement.NormalizedName)) {
            throw "The Edge TTS wheel manifest is missing license provenance for $($requirement.Name)."
        }
    }

    if (-not $AllowAdditionalDistributions) {
        $expectedNames = @($PinnedRequirements | ForEach-Object { $_.NormalizedName } | Sort-Object)
        $installedNames = @($installedByName.Keys | Sort-Object)
        if (($expectedNames.Count -ne $installedNames.Count) -or
            (($expectedNames -join "`n") -cne ($installedNames -join "`n"))) {
            throw "The Edge TTS runtime contains missing or unexpected installed distributions. Expected:`n$($expectedNames -join [Environment]::NewLine)`nFound:`n$($installedNames -join [Environment]::NewLine)"
        }
    }
}

function Get-EdgeTtsRuntimeInventoryData {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeRoot,
        [Parameter(Mandatory = $true)]
        [string]$RequirementsPath,
        [Parameter(Mandatory = $true)]
        [string]$WheelManifestPath,
        [Parameter(Mandatory = $true)]
        [object[]]$PinnedRequirements,
        [Parameter(Mandatory = $true)]
        [object]$WheelManifest,
        [Parameter(Mandatory = $true)]
        [object]$PythonSourceLock
    )

    Assert-EdgeTtsRuntimeLicenseInventory -RuntimeRoot $RuntimeRoot -PinnedRequirements $PinnedRequirements `
        -WheelManifest $WheelManifest -PythonSourceLock $PythonSourceLock -AllowAdditionalDistributions

    $runtime = Assert-CompleteDirectory -Path $RuntimeRoot -Description "Edge TTS runtime inventory root"
    $sitePackages = Assert-CompleteDirectory -Path (Join-Path $runtime.FullName 'Lib\site-packages') `
        -Description "Edge TTS runtime inventory site-packages"
    $packages = [System.Collections.Generic.List[object]]::new()
    foreach ($requirement in @($PinnedRequirements | Sort-Object NormalizedName)) {
        $directory = @(Get-ChildItem -LiteralPath $sitePackages.FullName -Directory -Filter '*.dist-info' -Force | Where-Object {
            $metadataPath = Join-Path $_.FullName 'METADATA'
            if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { return $false }
            $metadata = [System.IO.File]::ReadAllText($metadataPath)
            $nameMatch = [regex]::Match($metadata, '(?mi)^Name:\s*(?<value>[^\r\n]+)')
            if (-not $nameMatch.Success) { return $false }
            $normalized = $nameMatch.Groups['value'].Value.Trim().ToLowerInvariant().Replace('_', '-').Replace('.', '-')
            return $normalized -ceq $requirement.NormalizedName
        })
        if ($directory.Count -ne 1) {
            throw "Could not uniquely identify installed metadata for $($requirement.Name)."
        }
        $manifestEntry = @($WheelManifest.files | Where-Object {
            ([string]$_.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-') -ceq $requirement.NormalizedName
        })[0]
        $licenseFiles = [System.Collections.Generic.List[object]]::new()
        foreach ($licenseEntry in @($manifestEntry.licenseFiles | Sort-Object relativePath)) {
            $relative = ([string]$licenseEntry.relativePath).Replace('\', '/')
            $path = Join-Path $directory[0].FullName ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            $item = Assert-CompleteFile -Path $path -Description "Edge TTS runtime inventory license"
            $licenseFiles.Add([ordered]@{
                relativePath = $relative
                sizeBytes = [Int64]$item.Length
                sha256 = Get-CompleteSha256 -Path $item.FullName
            })
        }
        $packages.Add([ordered]@{
            distribution = [string]$manifestEntry.distribution
            version = [string]$requirement.Version
            distInfoRelativePath = Get-CompleteRelativePath -BasePath $runtime.FullName -TargetPath $directory[0].FullName
            wheelSha256 = [string]$manifestEntry.sha256
            licenseDeclared = [string]$manifestEntry.licenseDeclared
            licenseFiles = @($licenseFiles)
        })
    }

    $pythonLicense = Assert-CompleteFile -Path (Join-Path $runtime.FullName 'LICENSE.txt') -Description "Bundled CPython license"
    return [ordered]@{
        schemaVersion = 1
        pythonVersion = [string]$PythonSourceLock.version
        edgeTtsVersion = '7.2.8'
        requirementsSha256 = Get-CompleteSha256 -Path $RequirementsPath
        wheelManifestSha256 = Get-CompleteSha256 -Path $WheelManifestPath
        pythonLicense = [ordered]@{
            relativePath = 'LICENSE.txt'
            sizeBytes = [Int64]$pythonLicense.Length
            sha256 = Get-CompleteSha256 -Path $pythonLicense.FullName
        }
        packages = @($packages)
    }
}

function Write-EdgeTtsRuntimeInventory {
    param(
        [Parameter(Mandatory = $true)] [string]$RuntimeRoot,
        [Parameter(Mandatory = $true)] [string]$RequirementsPath,
        [Parameter(Mandatory = $true)] [string]$WheelManifestPath,
        [Parameter(Mandatory = $true)] [object[]]$PinnedRequirements,
        [Parameter(Mandatory = $true)] [object]$WheelManifest,
        [Parameter(Mandatory = $true)] [object]$PythonSourceLock,
        [Parameter(Mandatory = $true)] [string]$InventoryPath
    )

    $inventory = Get-EdgeTtsRuntimeInventoryData -RuntimeRoot $RuntimeRoot -RequirementsPath $RequirementsPath `
        -WheelManifestPath $WheelManifestPath -PinnedRequirements $PinnedRequirements `
        -WheelManifest $WheelManifest -PythonSourceLock $PythonSourceLock
    Write-CompleteJson -Value $inventory -Path $InventoryPath
}

function Assert-EdgeTtsRuntimeInventory {
    param(
        [Parameter(Mandatory = $true)] [string]$RuntimeRoot,
        [Parameter(Mandatory = $true)] [string]$RequirementsPath,
        [Parameter(Mandatory = $true)] [string]$WheelManifestPath,
        [Parameter(Mandatory = $true)] [object[]]$PinnedRequirements,
        [Parameter(Mandatory = $true)] [object]$WheelManifest,
        [Parameter(Mandatory = $true)] [object]$PythonSourceLock,
        [Parameter(Mandatory = $true)] [string]$InventoryPath
    )

    $actual = Read-CompleteJson -Path $InventoryPath -Description "Edge TTS runtime inventory"
    $expected = Get-EdgeTtsRuntimeInventoryData -RuntimeRoot $RuntimeRoot -RequirementsPath $RequirementsPath `
        -WheelManifestPath $WheelManifestPath -PinnedRequirements $PinnedRequirements `
        -WheelManifest $WheelManifest -PythonSourceLock $PythonSourceLock
    $actualJson = $actual | ConvertTo-Json -Depth 12 -Compress
    $expectedJson = $expected | ConvertTo-Json -Depth 12 -Compress
    if ($actualJson -cne $expectedJson) {
        throw "The staged Edge TTS runtime inventory does not match the audited runtime, lock, manifest, and license files."
    }
}

function Expand-EdgeTtsSafeZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ZipPath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,
        [Int64]$MaximumExpandedBytes = 1073741824
    )

    Assert-CompleteFile -Path $ZipPath -Description "Edge TTS Python archive" | Out-Null
    if (Test-Path -LiteralPath $DestinationPath) {
        throw "Refusing to extract the Edge TTS runtime into an existing directory: $DestinationPath"
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
                throw "The Edge TTS Python archive contains an empty entry."
            }
            if ($entryName.EndsWith('/')) {
                continue
            }
            Assert-CompleteSafeRelativePath -RelativePath $entryName -Description "Edge TTS Python archive entry"
            if ($seen.ContainsKey($entryName)) {
                throw "The Edge TTS Python archive contains a duplicate entry: $entryName"
            }
            $seen[$entryName] = $true

            $expandedBytes += [Int64]$entry.Length
            if ($expandedBytes -gt $MaximumExpandedBytes) {
                throw "The Edge TTS Python archive exceeds the maximum permitted expanded size."
            }

            $target = [System.IO.Path]::GetFullPath((Join-Path $DestinationPath ($entryName.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
            if (-not $target.StartsWith($destinationFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "The Edge TTS Python archive contains a path that escapes its extraction root: $entryName"
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

    Assert-CompleteTreeNoReparsePoints -RootPath $DestinationPath -Description "Edge TTS Python extraction"
}

function Initialize-EdgeTtsRuntime {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PythonArchive,
        [Parameter(Mandatory = $true)]
        [string]$RuntimeRoot,
        [Parameter(Mandatory = $true)]
        [string]$WheelRoot,
        [Parameter(Mandatory = $true)]
        [string]$RequirementsPath,
        [Parameter(Mandatory = $true)]
        [string]$BuildPython,
        [Parameter(Mandatory = $true)]
        [object[]]$PinnedRequirements
    )

    Expand-EdgeTtsSafeZip -ZipPath $PythonArchive -DestinationPath $RuntimeRoot
    $python = Join-Path $RuntimeRoot "python.exe"
    Assert-CompleteFile -Path $python -Description "Edge TTS embedded Python executable" | Out-Null

    $pthFiles = @(Get-ChildItem -LiteralPath $RuntimeRoot -Filter "python*._pth" -File -Force)
    if ($pthFiles.Count -ne 1) {
        throw "The Edge TTS Python runtime must contain exactly one python*._pth file."
    }
    [System.IO.File]::WriteAllLines(
        $pthFiles[0].FullName,
        @('python314.zip', '.', 'Lib\site-packages', 'import site'),
        [System.Text.ASCIIEncoding]::new())

    $sitePackages = Join-Path $RuntimeRoot "Lib\site-packages"
    New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null
    $previousNoByteCode = $env:PYTHONDONTWRITEBYTECODE
    $env:PYTHONDONTWRITEBYTECODE = '1'
    try {
        $versionOutput = @(& $python -c "import sys; print('.'.join(map(str, sys.version_info[:3])))" 2>&1)
        if ($LASTEXITCODE -ne 0 -or (($versionOutput -join "`n").Trim() -cne '3.14.6')) {
            throw "The Edge TTS embedded runtime did not report exactly Python 3.14.6. Output: $($versionOutput -join ' ')"
        }

        $installOutput = @(& $BuildPython -m pip --isolated install --disable-pip-version-check --ignore-installed `
            --no-index --no-deps --no-compile --no-warn-script-location `
            --find-links $WheelRoot --target $sitePackages -r $RequirementsPath 2>&1)
        $installExitCode = $LASTEXITCODE
        $installOutput | ForEach-Object { Write-Host $_ }
        if ($installExitCode -ne 0) {
            throw "Offline installation of the pinned Edge TTS wheel set failed."
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
        foreach ($requirement in $PinnedRequirements) {
            $expectedVersions[$requirement.NormalizedName] = $requirement.Version
        }
        $checkCode = @'
import importlib.metadata as metadata
import json
from pathlib import Path
import sys
import edge_tts

expected = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
actual = {name: metadata.version(name) for name in expected}
if actual != expected:
    raise SystemExit("installed distribution versions differ: " + json.dumps({"expected": expected, "actual": actual}, sort_keys=True))
if metadata.version("edge-tts") != "7.2.8":
    raise SystemExit("unexpected edge-tts version")
print(json.dumps({"python": ".".join(map(str, sys.version_info[:3])), "edgeTts": metadata.version("edge-tts")}, sort_keys=True))
'@
        $runtimeCheckScript = Join-Path $RuntimeRoot ".speak-edge-runtime-check.py"
        $runtimeExpectedJson = Join-Path $RuntimeRoot ".speak-edge-runtime-expected.json"
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
            throw "Edge TTS embedded runtime import/version check failed: $($runtimeCheck -join [Environment]::NewLine)"
        }

        $moduleVersion = @(& $python -B -I -m edge_tts --version 2>&1)
        if ($LASTEXITCODE -ne 0 -or (($moduleVersion -join "`n").Trim() -cne 'edge-tts 7.2.8')) {
            throw "Edge TTS module launcher check failed: $($moduleVersion -join ' ')"
        }

        Get-ChildItem -LiteralPath $RuntimeRoot -Directory -Recurse -Filter "__pycache__" |
            Sort-Object FullName -Descending |
            Remove-Item -Recurse -Force
        Get-ChildItem -LiteralPath $RuntimeRoot -File -Recurse |
            Where-Object { $_.Extension -in @('.pyc', '.pyo') } |
            Remove-Item -Force
    }
    finally {
        $env:PYTHONDONTWRITEBYTECODE = $previousNoByteCode
    }

    Assert-CompleteTreeNoReparsePoints -RootPath $RuntimeRoot -Description "Installed Edge TTS runtime"
    return $python
}
