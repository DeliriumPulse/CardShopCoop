# CardShopCoop - Stage the mod layer onto a USB drive (run on DAD's PC)
# Copies everything the son's PC needs: BepInEx (mods + configs + CardShopCoop),
# the BepInEx loader files, and the setup script. The game itself is NOT copied -
# the son installs it from Steam.
#
# Usage:  Right-click > Run with PowerShell   (or:  .\Stage-ModsToUSB.ps1 -Target E:\)

param([string]$Target = "")

$ErrorActionPreference = "Stop"
$game = "C:\Program Files (x86)\Steam\steamapps\common\TCG Card Shop Simulator"

if (-not (Test-Path "$game\BepInEx")) { Write-Host "Game/BepInEx not found at $game" -ForegroundColor Red; exit 1 }

if ($Target -eq "") {
    Write-Host "Removable drives:" -ForegroundColor Cyan
    Get-Volume | Where-Object DriveType -eq 'Removable' | Format-Table DriveLetter, FileSystemLabel, @{n='FreeGB';e={[math]::Round($_.SizeRemaining/1GB,1)}}
    $Target = Read-Host "Enter target drive or folder (e.g. E:\)"
}

$dest = Join-Path $Target "CardShopCoop-Setup"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# prefer the real home-LAN address (192.168.*) over virtual/VPN adapters
$candidates = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike "169.254*" -and $_.IPAddress -ne "127.0.0.1" } | ForEach-Object { $_.IPAddress }
$myIp = ($candidates | Where-Object { $_ -like "192.168.*" } | Select-Object -First 1)
if (-not $myIp) { $myIp = ($candidates | Where-Object { $_ -like "10.*" } | Select-Object -First 1) }
if (-not $myIp) { $myIp = $candidates | Select-Object -First 1 }
Set-Content -Path (Join-Path $dest "dad-ip.txt") -Value $myIp
Write-Host "Dad's home-LAN IP recorded: $myIp" -ForegroundColor Green

Write-Host "Copying mod layer (this is ~57 GB - grab a snack)..." -ForegroundColor Cyan
robocopy "$game\BepInEx" "$dest\BepInEx" /E /XD cache /XF LogOutput.log "CardShopCoop_*.log" /NFL /NDL /NP
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "steam_appid.txt") {
    if (Test-Path "$game\$f") { Copy-Item "$game\$f" $dest -Force }
}
Copy-Item (Join-Path $PSScriptRoot "Setup-SonPC.ps1") $dest -Force
Copy-Item (Join-Path $PSScriptRoot "PLAY_GUIDE.md") $dest -Force -ErrorAction SilentlyContinue

Write-Host "`nDone! Take the USB to the other PC and run Setup-SonPC.ps1 from it." -ForegroundColor Green
