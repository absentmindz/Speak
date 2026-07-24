param(
    [string]$SourceDir = "",
    [string]$InstallDir = "$env:ProgramFiles\Speak",
    [switch]$Silent = $false
)

$ErrorActionPreference = "Stop"
$AppName = "Speak"
$AppExe = "Speak.exe"
$Publisher = "Hamza"
$RepoUrl = ""

if (-not $SourceDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $candidates = @(
        "$scriptDir\publish-output",
        "$scriptDir\publish\Speak-win-x64-warm-model",
        "$scriptDir\bin\Release\net8.0-windows\win-x64\publish",
        "$scriptDir\bin\Release\net8.0-windows\publish"
    )
    foreach ($c in $candidates) {
        if (Test-Path "$c\$AppExe") {
            $SourceDir = $c
            break
        }
    }
    if (-not $SourceDir) {
        $choices = Get-ChildItem "$scriptDir\publish\*" -Directory -ErrorAction SilentlyContinue
        foreach ($d in $choices) {
            if (Test-Path "$d\$AppExe") {
                $SourceDir = $d.FullName
                break
            }
        }
    }
}

if (-not $SourceDir -or -not (Test-Path "$SourceDir\$AppExe")) {
    Write-Host "ERROR: Cannot find $AppExe. Run 'dotnet publish Speak.csproj -o publish-output' first." -ForegroundColor Red
    if (-not $Silent) { Read-Host "Press Enter to exit" }
    exit 1
}

Write-Host "Installing $AppName from: $SourceDir" -ForegroundColor Cyan
Write-Host "Target directory: $InstallDir" -ForegroundColor Cyan
Write-Host ""

# Stop any running instance
$existing = Get-Process -Name $AppName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping running instance..." -ForegroundColor Yellow
    $existing | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# Create install directory
if (Test-Path $InstallDir) {
    Remove-Item "$InstallDir\*" -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# Copy all files
Write-Host "Copying files..." -ForegroundColor Green
Copy-Item "$SourceDir\*" $InstallDir -Recurse -Force

# Quiet down localization folder noise
Write-Host "Installed $((Get-ChildItem $InstallDir -Recurse -File | Measure-Object).Count) files" -ForegroundColor Green

# Create Start Menu shortcut
$startMenu = [Environment]::GetFolderPath("CommonStartMenu")
$shortcutDir = "$startMenu\Programs\$AppName"
New-Item -ItemType Directory -Path $shortcutDir -Force | Out-Null

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut("$shortcutDir\$AppName.lnk")
$shortcut.TargetPath = "$InstallDir\$AppExe"
$shortcut.WorkingDirectory = $InstallDir
$shortcut.IconLocation = "$InstallDir\speak.ico"
$shortcut.Description = "Premium local dictation for Windows"
$shortcut.Save()

# Desktop shortcut
$desktop = [Environment]::GetFolderPath("CommonDesktopDirectory")
$desktopShortcut = $shell.CreateShortcut("$desktop\$AppName.lnk")
$desktopShortcut.TargetPath = "$InstallDir\$AppExe"
$desktopShortcut.WorkingDirectory = $InstallDir
$desktopShortcut.IconLocation = "$InstallDir\speak.ico"
$desktopShortcut.Description = "Premium local dictation for Windows"
$desktopShortcut.Save()

# Uninstall registry entry
$uninstallKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
if (-not (Test-Path $uninstallKey)) {
    New-Item -Path $uninstallKey -Force | Out-Null
}
Set-ItemProperty -Path $uninstallKey -Name "DisplayName" -Value "$AppName" -Type String
Set-ItemProperty -Path $uninstallKey -Name "DisplayVersion" -Value "0.5.0" -Type String
Set-ItemProperty -Path $uninstallKey -Name "Publisher" -Value "$Publisher" -Type String
Set-ItemProperty -Path $uninstallKey -Name "InstallLocation" -Value "$InstallDir" -Type String
Set-ItemProperty -Path $uninstallKey -Name "DisplayIcon" -Value "$InstallDir\speak.ico" -Type String
Set-ItemProperty -Path $uninstallKey -Name "UninstallString" -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`"" -Type String
Set-ItemProperty -Path $uninstallKey -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name "NoRepair" -Value 1 -Type DWord

# Create uninstall script
$uninstallContent = @"
`$ErrorActionPreference = "Stop"
Write-Host "Uninstalling $AppName..." -ForegroundColor Yellow

# Stop running instance
`$proc = Get-Process -Name "$AppName" -ErrorAction SilentlyContinue
if (`$proc) { `$proc | Stop-Process -Force; Start-Sleep 1 }

# Remove install directory
if (Test-Path "$InstallDir") {
    Remove-Item "$InstallDir" -Recurse -Force -ErrorAction SilentlyContinue
}

# Remove shortcuts
Remove-Item "$shortcutDir" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$desktop\$AppName.lnk" -Force -ErrorAction SilentlyContinue

# Remove uninstall key
Remove-Item "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppName" -Force -ErrorAction SilentlyContinue

Write-Host "Uninstalled successfully." -ForegroundColor Green
Read-Host "Press Enter to exit"
"@
Set-Content -Path "$InstallDir\uninstall.ps1" -Value $uninstallContent -Encoding UTF8

# Create uninstall .bat (double-clickable)
$uninstallBat = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0uninstall.ps1`"`r`npause"
Set-Content -Path "$InstallDir\uninstall.bat" -Value $uninstallBat -Encoding ASCII

Write-Host ""
Write-Host "Installation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Launch from: Start Menu > $AppName"
Write-Host "Uninstall:  $InstallDir\uninstall.bat"
Write-Host ""

$launch = $true
if (-not $Silent) {
    $response = Read-Host "Launch $AppName now? (Y/n)"
    $launch = ($response -eq "" -or $response -eq "Y" -or $response -eq "y")
}

if ($launch) {
    Write-Host "Starting $AppName..." -ForegroundColor Cyan
    Start-Process "$InstallDir\$AppExe" -WorkingDirectory $InstallDir
}

if (-not $Silent) {
    Read-Host "Press Enter to exit"
}
