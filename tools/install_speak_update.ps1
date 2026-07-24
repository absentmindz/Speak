$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$source = [System.IO.Path]::GetFullPath("D:\Speak\Latest")
$destination = [System.IO.Path]::GetFullPath("C:\Program Files\Speak")
$backup = [System.IO.Path]::GetFullPath("D:\Speak\install-backups\Speak-before-audio-studio-keepalive-20260711")

if ($source -ne "D:\Speak\Latest" -or $destination -ne "C:\Program Files\Speak") {
    throw "Unexpected Speak update path."
}

if (-not (Test-Path -LiteralPath (Join-Path $source "Speak.dll"))) {
    throw "Verified Speak release is missing."
}

if (-not (Test-Path -LiteralPath (Join-Path $backup "Speak.dll"))) {
    throw "Installed Speak backup is missing."
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $destination -Recurse -Force

$sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $source "Speak.dll")).Hash
$installedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $destination "Speak.dll")).Hash
if ($sourceHash -ne $installedHash) {
    throw "Installed Speak verification failed. The original backup is available at $backup."
}

Write-Host "Speak update installed and verified."
