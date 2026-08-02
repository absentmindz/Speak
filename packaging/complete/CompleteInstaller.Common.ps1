Set-StrictMode -Version Latest

function Get-CompleteFullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-CompleteNoReparseAncestors {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $current = Get-CompleteFullPath -Path $Path
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description traverses a reparse point: $current"
            }
        }

        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $current) {
            break
        }
        $current = $parent
    }
}

function Assert-CompleteFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description must not be a reparse point: $Path"
    }
    Assert-CompleteNoReparseAncestors -Path $item.FullName -Description $Description

    return $item
}

function Assert-CompleteDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description is missing: $Path"
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description must not be a reparse point: $Path"
    }
    Assert-CompleteNoReparseAncestors -Path $item.FullName -Description $Description

    return $item
}

function Get-CompleteRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,
        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    $base = (Get-CompleteFullPath -Path $BasePath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $target = Get-CompleteFullPath -Path $TargetPath
    $baseUri = [Uri]::new($base)
    $targetUri = [Uri]::new($target)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('\', '/')
}

function Assert-CompleteSafeRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '^[A-Za-z]:' -or
        $RelativePath -match '(^|[\\/])\.\.([\\/]|$)' -or
        $RelativePath -match '[\x00-\x1f]') {
        throw "$Description has an unsafe relative path: $RelativePath"
    }
}

function Get-CompleteTreeFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $root = Assert-CompleteDirectory -Path $RootPath -Description $Description
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($root.FullName)

    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $children = @(Get-ChildItem -LiteralPath $directory -Force)
        foreach ($child in $children) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description contains a reparse point: $($child.FullName)"
            }

            if ($child.PSIsContainer) {
                $pending.Push($child.FullName)
            }
            else {
                Write-Output $child
            }
        }
    }
}

function Assert-CompleteTreeNoReparsePoints {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $null = @(Get-CompleteTreeFiles -RootPath $RootPath -Description $Description)
}

function Get-CompleteSha256 {
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

function Assert-CompleteExactFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description,
        [Parameter(Mandatory = $true)]
        [Int64]$ExpectedSizeBytes,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedSha256,
        [switch]$AllowEmpty
    )

    if ($ExpectedSizeBytes -lt 0 -or ($ExpectedSizeBytes -eq 0 -and -not $AllowEmpty)) {
        throw "$Description has an invalid expected size."
    }
    if ($ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Description has an invalid expected SHA-256."
    }

    $item = Assert-CompleteFile -Path $Path -Description $Description
    if ($item.Length -ne $ExpectedSizeBytes) {
        throw "$Description has an unexpected size. Expected $ExpectedSizeBytes bytes; found $($item.Length): $Path"
    }

    $actualSha256 = Get-CompleteSha256 -Path $item.FullName
    if ($actualSha256 -cne $ExpectedSha256.ToLowerInvariant()) {
        throw "$Description SHA-256 mismatch. Expected $ExpectedSha256; found ${actualSha256}: $Path"
    }

    return $item
}

function Read-CompleteJson {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    Assert-CompleteFile -Path $Path -Description $Description | Out-Null
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "$Description is not valid JSON: $Path. $($_.Exception.Message)"
    }
}

function Assert-CompletePythonRuntimeLicenseInventory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeRoot,
        [Parameter(Mandatory = $true)]
        [object[]]$PinnedRequirements,
        [Parameter(Mandatory = $true)]
        [object[]]$WheelManifests
    )

    $runtime = Assert-CompleteDirectory -Path $RuntimeRoot -Description "Embedded Python runtime"
    $pythonLicense = Assert-CompleteFile -Path (Join-Path $runtime.FullName 'LICENSE.txt') `
        -Description "Embedded CPython license"
    if ($pythonLicense.Length -le 0) {
        throw "The embedded CPython license is empty."
    }

    $expectedByName = @{}
    foreach ($requirement in $PinnedRequirements) {
        $normalized = [string]$requirement.NormalizedName
        if ($expectedByName.ContainsKey($normalized) -and
            [string]$expectedByName[$normalized] -cne [string]$requirement.Version) {
            throw "Embedded Python requirement locks conflict for $($requirement.Name)."
        }
        $expectedByName[$normalized] = [string]$requirement.Version
    }

    $manifestByName = @{}
    foreach ($manifest in $WheelManifests) {
        foreach ($entry in @($manifest.files)) {
            $normalized = ([string]$entry.distribution).ToLowerInvariant().Replace('_', '-').Replace('.', '-')
            foreach ($property in @('version', 'sha256', 'licenseDeclared')) {
                if ($null -eq $entry.PSObject.Properties[$property] -or
                    [string]::IsNullOrWhiteSpace([string]$entry.$property)) {
                    throw "Python wheel manifest lacks $property for $($entry.distribution)."
                }
            }
            if ([string]$entry.licenseDeclared -ceq 'NOASSERTION') {
                throw "Python wheel manifest lacks an audited license for $($entry.distribution)."
            }
            if ($manifestByName.ContainsKey($normalized)) {
                $existing = $manifestByName[$normalized]
                if ([string]$existing.version -cne [string]$entry.version -or
                    [string]$existing.sha256 -cne [string]$entry.sha256 -or
                    [string]$existing.licenseDeclared -cne [string]$entry.licenseDeclared) {
                    throw "Python wheel manifests disagree for $($entry.distribution)."
                }
            }
            else {
                $manifestByName[$normalized] = $entry
            }
        }
    }

    foreach ($normalized in $expectedByName.Keys) {
        if (-not $manifestByName.ContainsKey($normalized)) {
            throw "Python wheel manifests lack provenance or license data for $normalized."
        }
        if ([string]$manifestByName[$normalized].version -cne [string]$expectedByName[$normalized]) {
            throw "Python wheel manifest version mismatch for $normalized."
        }
    }

    $sitePackages = Assert-CompleteDirectory -Path (Join-Path $runtime.FullName 'Lib\site-packages') `
        -Description "Embedded Python site-packages"
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
            throw "Embedded Python contains duplicate installed metadata for $name."
        }
        $declaredLicensePaths = @([regex]::Matches($metadata, '(?mi)^License-File:\s*(?<value>[^\r\n]+)') | ForEach-Object {
            $_.Groups['value'].Value.Trim().Replace('\', '/')
        })
        if ($declaredLicensePaths.Count -eq 0) {
            throw "Installed Python distribution $name metadata does not declare its packaged license files."
        }
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
        }
        $installedByName[$normalized] = [pscustomobject]@{
            Name = $name
            Version = $versionMatch.Groups['value'].Value.Trim()
        }
    }

    $expectedNames = @($expectedByName.Keys | Sort-Object)
    $installedNames = @($installedByName.Keys | Sort-Object)
    if (($expectedNames.Count -ne $installedNames.Count) -or
        (($expectedNames -join "`n") -cne ($installedNames -join "`n"))) {
        throw "Embedded Python contains missing or unexpected distributions. Expected:`n$($expectedNames -join [Environment]::NewLine)`nFound:`n$($installedNames -join [Environment]::NewLine)"
    }
    foreach ($normalized in $expectedNames) {
        if ([string]$installedByName[$normalized].Version -cne [string]$expectedByName[$normalized]) {
            throw "Embedded Python installed version mismatch for $normalized."
        }
    }
}

function Assert-CompleteExpectedFileSet {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [object[]]$ExpectedFiles,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $root = Assert-CompleteDirectory -Path $RootPath -Description $Description
    $expected = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $ExpectedFiles) {
        if ($null -eq $entry -or $null -eq $entry.relativePath) {
            throw "$Description manifest contains an entry without relativePath."
        }
        $relative = ([string]$entry.relativePath).Replace('\', '/')
        Assert-CompleteSafeRelativePath -RelativePath $relative -Description "$Description manifest"
        if ($expected.Contains($relative)) {
            throw "$Description manifest contains a duplicate path: $relative"
        }
        $expected.Add($relative)
    }

    $actual = [System.Collections.Generic.List[string]]::new()
    foreach ($file in @(Get-CompleteTreeFiles -RootPath $root.FullName -Description $Description)) {
        $relative = Get-CompleteRelativePath -BasePath $root.FullName -TargetPath $file.FullName
        Assert-CompleteSafeRelativePath -RelativePath $relative -Description $Description
        $actual.Add($relative)
    }

    $expectedSorted = @($expected | Sort-Object)
    $actualSorted = @($actual | Sort-Object)
    if (($expectedSorted.Count -ne $actualSorted.Count) -or
        (($expectedSorted -join "`n") -cne ($actualSorted -join "`n"))) {
        throw "$Description has missing or unexpected files. Expected:`n$($expectedSorted -join [Environment]::NewLine)`nFound:`n$($actualSorted -join [Environment]::NewLine)"
    }

    foreach ($entry in $ExpectedFiles) {
        $relative = ([string]$entry.relativePath).Replace('\', '/')
        $candidate = Join-Path $root.FullName ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        Assert-CompleteExactFile -Path $candidate -Description "$Description file $relative" `
            -ExpectedSizeBytes ([Int64]$entry.sizeBytes) -ExpectedSha256 ([string]$entry.sha256) | Out-Null
    }
}

function Copy-CompleteExactTree {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot,
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot,
        [Parameter(Mandatory = $true)]
        [object[]]$ExpectedFiles,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $source = Assert-CompleteDirectory -Path $SourceRoot -Description "$Description source"
    Assert-CompleteExpectedFileSet -RootPath $source.FullName -ExpectedFiles $ExpectedFiles -Description "$Description source"

    if (Test-Path -LiteralPath $DestinationRoot) {
        throw "Refusing to copy $Description into an existing destination: $DestinationRoot"
    }
    New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null

    foreach ($entry in $ExpectedFiles) {
        $relative = ([string]$entry.relativePath).Replace('\', '/')
        $sourceFile = Join-Path $source.FullName ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destinationFile = Join-Path $DestinationRoot ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destinationDirectory = Split-Path -Parent $destinationFile
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile -Destination $destinationFile -Force
        Assert-CompleteExactFile -Path $destinationFile -Description "$Description copied file $relative" `
            -ExpectedSizeBytes ([Int64]$entry.sizeBytes) -ExpectedSha256 ([string]$entry.sha256) | Out-Null
    }

    Assert-CompleteExpectedFileSet -RootPath $DestinationRoot -ExpectedFiles $ExpectedFiles -Description "$Description destination"
}

function Assert-CompleteSelectedFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [object[]]$ExpectedFiles,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $root = Assert-CompleteDirectory -Path $RootPath -Description $Description
    Assert-CompleteTreeNoReparsePoints -RootPath $root.FullName -Description $Description
    $seen = @{}

    foreach ($entry in $ExpectedFiles) {
        if ($null -eq $entry -or $null -eq $entry.relativePath) {
            throw "$Description manifest contains an entry without relativePath."
        }

        $relative = ([string]$entry.relativePath).Replace('\', '/')
        Assert-CompleteSafeRelativePath -RelativePath $relative -Description "$Description manifest"
        if ($seen.ContainsKey($relative)) {
            throw "$Description manifest contains a duplicate path: $relative"
        }
        $seen[$relative] = $true

        $candidate = Join-Path $root.FullName ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        Assert-CompleteExactFile -Path $candidate -Description "$Description file $relative" `
            -ExpectedSizeBytes ([Int64]$entry.sizeBytes) -ExpectedSha256 ([string]$entry.sha256) | Out-Null
    }
}

function Copy-CompleteSelectedFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot,
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot,
        [Parameter(Mandatory = $true)]
        [object[]]$ExpectedFiles,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $source = Assert-CompleteDirectory -Path $SourceRoot -Description "$Description source"
    Assert-CompleteSelectedFiles -RootPath $source.FullName -ExpectedFiles $ExpectedFiles -Description "$Description source"

    if (Test-Path -LiteralPath $DestinationRoot) {
        throw "Refusing to copy $Description into an existing destination: $DestinationRoot"
    }
    New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null

    foreach ($entry in $ExpectedFiles) {
        $relative = ([string]$entry.relativePath).Replace('\', '/')
        $sourceFile = Join-Path $source.FullName ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destinationFile = Join-Path $DestinationRoot ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destinationDirectory = Split-Path -Parent $destinationFile
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile -Destination $destinationFile -Force
        Assert-CompleteExactFile -Path $destinationFile -Description "$Description copied file $relative" `
            -ExpectedSizeBytes ([Int64]$entry.sizeBytes) -ExpectedSha256 ([string]$entry.sha256) | Out-Null
    }

    Assert-CompleteExpectedFileSet -RootPath $DestinationRoot -ExpectedFiles $ExpectedFiles -Description "$Description destination"
}

function Reset-CompleteGeneratedDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedParent,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedLeaf
    )

    $fullPath = Get-CompleteFullPath -Path $Path
    $parentPath = Get-CompleteFullPath -Path $ExpectedParent
    $actualParent = Split-Path -Parent $fullPath
    if (($actualParent -cne $parentPath) -or
        ([System.IO.Path]::GetFileName($fullPath) -cne $ExpectedLeaf)) {
        throw "Refusing to clear an unexpected generated directory: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Assert-CompleteTreeNoReparsePoints -RootPath $fullPath -Description "Generated directory slated for cleanup"
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    return $fullPath
}

function Assert-CompleteAuthenticodeSignature {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description,
        [Parameter(Mandatory = $true)]
        [string]$SubjectRegex,
        [Parameter(Mandatory = $true)]
        [string]$IssuerRegex
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "$Description does not have a valid Authenticode signature: $($signature.Status) $($signature.StatusMessage)"
    }
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch $SubjectRegex -or
        $signature.SignerCertificate.Issuer -notmatch $IssuerRegex) {
        throw "$Description signer identity did not match the audited certificate policy."
    }
}

function Invoke-CompleteSigstoreVerification {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SigstorePath,
        [Parameter(Mandatory = $true)]
        [string]$ArtifactPath,
        [Parameter(Mandatory = $true)]
        [string]$BundlePath,
        [Parameter(Mandatory = $true)]
        [string]$Identity,
        [Parameter(Mandatory = $true)]
        [string]$OidcIssuer
    )

    Assert-CompleteFile -Path $SigstorePath -Description "sigstore verifier" | Out-Null
    Assert-CompleteFile -Path $ArtifactPath -Description "Sigstore-signed artifact" | Out-Null
    Assert-CompleteFile -Path $BundlePath -Description "Sigstore bundle" | Out-Null

    $help = @(& $SigstorePath verify identity --help 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect the sigstore verifier help text."
    }

    $offlineArguments = if ($help -match '--offline') { @('--offline') } else { @() }
    if ($help -match '--cert-identity') {
        $arguments = @('verify', 'identity') + $offlineArguments + @('--bundle', $BundlePath, '--cert-identity', $Identity, '--cert-oidc-issuer', $OidcIssuer, $ArtifactPath)
    }
    elseif ($help -match '--certificate-identity') {
        $arguments = @('verify', 'identity') + $offlineArguments + @('--bundle', $BundlePath, '--certificate-identity', $Identity, '--certificate-oidc-issuer', $OidcIssuer, $ArtifactPath)
    }
    else {
        throw "The supplied sigstore verifier does not support identity verification flags required by the source lock."
    }

    & $SigstorePath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Sigstore verification failed for $ArtifactPath."
    }
}

function Get-CompleteFileInventory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [string[]]$ExcludedRelativePaths = @(),
        [hashtable]$ComponentRoots = @{}
    )

    $root = Assert-CompleteDirectory -Path $RootPath -Description "Component manifest root"
    $excluded = @{}
    foreach ($path in $ExcludedRelativePaths) {
        $excluded[$path.Replace('\', '/')] = $true
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($file in @(Get-CompleteTreeFiles -RootPath $root.FullName -Description "Component manifest root")) {
        $relative = Get-CompleteRelativePath -BasePath $root.FullName -TargetPath $file.FullName
        if ($excluded.ContainsKey($relative)) {
            continue
        }

        $component = 'Unclassified'
        foreach ($name in $ComponentRoots.Keys) {
            $prefix = ([string]$ComponentRoots[$name]).Trim('/').Replace('\', '/')
            if ($relative -ceq $prefix -or $relative.StartsWith($prefix + '/', [System.StringComparison]::Ordinal)) {
                $component = [string]$name
                break
            }
        }

        $entries.Add([ordered]@{
            component = $component
            relativePath = $relative
            sizeBytes = [Int64]$file.Length
            sha256 = Get-CompleteSha256 -Path $file.FullName
        })
    }

    return @($entries | Sort-Object relativePath)
}

function Write-CompleteJson {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $json = $Value | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText(
        (Get-CompleteFullPath -Path $Path),
        $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}
