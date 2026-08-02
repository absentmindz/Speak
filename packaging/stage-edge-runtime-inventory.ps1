$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. "$PSScriptRoot\complete\CompleteInstaller.Common.ps1"
. "$PSScriptRoot\EdgeTtsRuntime.Common.ps1"

$root = "$PSScriptRoot\stage\App\Runtime\EdgeTts\7.2.8"
$reqsPath = Join-Path $root "Support\requirements-edge-tts.lock.txt"
$manPath = Join-Path $root "Support\edge-tts-wheelhouse.manifest.json"
$pinned = Get-EdgeTtsPinnedRequirements -Path $reqsPath
$manifest = Get-Content $manPath -Raw | ConvertFrom-Json
Assert-EdgeTtsRuntimeInventory -RuntimeRoot $root -RequirementsPath $reqsPath -WheelManifestPath $manPath -PinnedRequirements $pinned -WheelManifest $manifest
Write-Output "Edge TTS runtime inventory generated OK"
if (Test-Path (Join-Path $root "Support\edge-tts-runtime.inventory.json")) {
    Write-Output "inventory.json exists ✓"
} else {
    throw "inventory.json not created"
}