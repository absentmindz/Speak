[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PackageIdentityName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Publisher,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PublisherDisplayName,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$PublishRoot,
    [string]$OutputPath,
    [string]$MakeAppxPath,
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
$templatePath = Join-Path $scriptRoot 'AppxManifest.xml.template'
$logoPath = Join-Path $repositoryRoot 'speak_logo.png'

if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
    throw "Manifest template not found: $templatePath"
}
if (-not (Test-Path -LiteralPath $logoPath -PathType Leaf)) {
    throw "Speak logo not found: $logoPath"
}
if ($PackageIdentityName.Contains('__') -or $Publisher.Contains('__') -or $PublisherDisplayName.Contains('__')) {
    throw 'Replace all placeholder identity values before validation or packaging.'
}
if ($PackageIdentityName -notmatch '^[A-Za-z0-9.-]{3,50}$') {
    throw 'PackageIdentityName must be 3-50 characters and contain only letters, numbers, periods, or hyphens.'
}
if ($Publisher -notmatch '^CN=') {
    throw 'Publisher must be the exact Partner Center publisher ID and normally starts with CN=.'
}

[int[]]$parts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($parts.Count -ne 4 -or $parts[0] -lt 1 -or $parts[3] -ne 0) {
    throw 'Store version must have four numeric parts, a non-zero first part, and a fourth part equal to 0.'
}
if (@($parts | Where-Object { $_ -lt 0 -or $_ -gt 65535 }).Count -ne 0) {
    throw 'Every Store version component must be between 0 and 65535.'
}

$escapedPackageIdentityName = [System.Security.SecurityElement]::Escape($PackageIdentityName)
$escapedPublisher = [System.Security.SecurityElement]::Escape($Publisher)
$escapedPublisherDisplayName = [System.Security.SecurityElement]::Escape($PublisherDisplayName)

$manifest = Get-Content -LiteralPath $templatePath -Raw
$manifest = $manifest.Replace('__PACKAGE_IDENTITY_NAME__', $escapedPackageIdentityName)
$manifest = $manifest.Replace('__PUBLISHER__', $escapedPublisher)
$manifest = $manifest.Replace('__PUBLISHER_DISPLAY_NAME__', $escapedPublisherDisplayName)
$manifest = $manifest.Replace('__VERSION__', $Version)
if ($manifest -match '__[A-Z0-9_]+__') {
    throw 'The rendered manifest still contains placeholders.'
}

try {
    [xml]$null = $manifest
}
catch {
    throw "Rendered manifest is not valid XML: $($_.Exception.Message)"
}

if ($ValidateOnly) {
    Write-Host "MSIX template validation passed for $PackageIdentityName version $Version."
    return
}

if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    throw 'PublishRoot is required unless -ValidateOnly is used.'
}
$resolvedPublishRoot = (Resolve-Path -LiteralPath $PublishRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $resolvedPublishRoot 'Speak.exe') -PathType Leaf)) {
    throw "PublishRoot does not contain Speak.exe: $resolvedPublishRoot"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot 'packaging\artifacts\Speak-Store.msix'
}
$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$resolvedOutputDirectory = Split-Path -Parent $resolvedOutputPath
New-Item -ItemType Directory -Force -Path $resolvedOutputDirectory | Out-Null

if ([string]::IsNullOrWhiteSpace($MakeAppxPath)) {
    $kitRoots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit')
    ) | Where-Object { Test-Path -LiteralPath $_ }

    $candidate = $kitRoots |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Filter makeappx.exe -ErrorAction SilentlyContinue } |
        Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' -or $_.DirectoryName -like '*App Certification Kit' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($null -eq $candidate) {
        throw 'MakeAppx.exe was not found. Install a current Windows SDK or pass -MakeAppxPath.'
    }
    $MakeAppxPath = $candidate.FullName
}
if (-not (Test-Path -LiteralPath $MakeAppxPath -PathType Leaf)) {
    throw "MakeAppx.exe not found: $MakeAppxPath"
}

$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('Speak-MSIX-' + [Guid]::NewGuid().ToString('N'))
$assetsRoot = Join-Path $stageRoot 'Assets'

function New-StoreImage {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][int]$Width,
        [Parameter(Mandatory = $true)][int]$Height
    )

    Add-Type -AssemblyName System.Drawing
    $sourceImage = [System.Drawing.Image]::FromFile($Source)
    try {
        $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $scale = [Math]::Min($Width / $sourceImage.Width, $Height / $sourceImage.Height)
                $drawWidth = [int][Math]::Round($sourceImage.Width * $scale)
                $drawHeight = [int][Math]::Round($sourceImage.Height * $scale)
                $x = [int](($Width - $drawWidth) / 2)
                $y = [int](($Height - $drawHeight) / 2)
                $graphics.DrawImage($sourceImage, $x, $y, $drawWidth, $drawHeight)
            }
            finally {
                $graphics.Dispose()
            }
            $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $sourceImage.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Force -Path $stageRoot, $assetsRoot | Out-Null
    Copy-Item -Path (Join-Path $resolvedPublishRoot '*') -Destination $stageRoot -Recurse -Force
    Set-Content -LiteralPath (Join-Path $stageRoot 'AppxManifest.xml') -Value $manifest -Encoding UTF8

    New-StoreImage -Source $logoPath -Destination (Join-Path $assetsRoot 'StoreLogo.png') -Width 50 -Height 50
    New-StoreImage -Source $logoPath -Destination (Join-Path $assetsRoot 'Square44x44Logo.png') -Width 44 -Height 44
    New-StoreImage -Source $logoPath -Destination (Join-Path $assetsRoot 'Square150x150Logo.png') -Width 150 -Height 150
    New-StoreImage -Source $logoPath -Destination (Join-Path $assetsRoot 'Wide310x150Logo.png') -Width 310 -Height 150
    New-StoreImage -Source $logoPath -Destination (Join-Path $assetsRoot 'Square310x310Logo.png') -Width 310 -Height 310
    New-StoreImage -Source $logoPath -Destination (Join-Path $assetsRoot 'SplashScreen.png') -Width 620 -Height 300

    & $MakeAppxPath pack /v /h SHA256 /d $stageRoot /p $resolvedOutputPath /o
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx failed with exit code $LASTEXITCODE."
    }

    Write-Host "Unsigned Store-candidate MSIX created: $resolvedOutputPath"
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
