# CardShopCoop - toggle a minimal "wire test" profile (CardShopCoop only, no content mods)
# Usage:  .\coop-vanilla-profile.ps1            -> switch to minimal profile (backs up saves first)
#         .\coop-vanilla-profile.ps1 -Restore   -> put the full mod stack back
param([switch]$Restore)

$ErrorActionPreference = "Stop"
$game = "C:\Program Files (x86)\Steam\steamapps\common\TCG Card Shop Simulator"
$plugins = "$game\BepInEx\plugins"
$parked = "$game\BepInEx\plugins.full"

if (Get-Process -Name "Card Shop Simulator" -ErrorAction SilentlyContinue) {
    Write-Host "Close the game first." -ForegroundColor Red; exit 1
}

if ($Restore) {
    if (-not (Test-Path $parked)) { Write-Host "Nothing to restore." -ForegroundColor Yellow; exit 0 }
    Get-ChildItem $parked | Move-Item -Destination $plugins -Force
    Remove-Item $parked -Force
    Write-Host "Full mod stack restored." -ForegroundColor Green
    exit 0
}

# safety: snapshot all saves before doing anything
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$saveDir = "$env:USERPROFILE\AppData\LocalLow\OPNeonGames\Card Shop Simulator"
Compress-Archive -Path "$saveDir\savedGames_*" -DestinationPath "C:\Users\zwhit\CardShopCoop\backup\saves-$stamp.zip" -Force
Write-Host "Saves backed up to CardShopCoop\backup\saves-$stamp.zip" -ForegroundColor Green

New-Item -ItemType Directory -Force -Path $parked | Out-Null
Get-ChildItem $plugins | Where-Object { $_.Name -ne "CardShopCoop" } | Move-Item -Destination $parked -Force
Write-Host @"
Minimal profile active: only CardShopCoop is loaded.
- Host a NEW GAME on a slot you don't mind replacing (your real saves are backed up).
- Your Pokemon shop save will NOT load correctly under this profile - don't open it.
- When done:  .\coop-vanilla-profile.ps1 -Restore
"@ -ForegroundColor Cyan
