# CardShopCoop - Set up the SON's PC (run FROM the USB folder created by Stage-ModsToUSB.ps1)
# 1) Finds TCG Card Shop Simulator (install it from Steam first and run it once!)
# 2) Copies the whole mod layer (BepInEx + all Pokemon mods + CardShopCoop)
# 3) Creates a "Play with Dad" desktop shortcut that auto-joins dad's game
#
# Usage:  Right-click > Run with PowerShell

$ErrorActionPreference = "Stop"
$src = $PSScriptRoot

# --- locate the game ---
$steam = (Get-ItemProperty -Path "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
$game = $null
if ($steam) {
    $libs = @($steam) + ((Get-Content "$steam\steamapps\libraryfolders.vdf" -ErrorAction SilentlyContinue |
        Select-String '"path"\s+"([^"]+)"').Matches | ForEach-Object { $_.Groups[1].Value -replace '\\\\','\' })
    foreach ($lib in $libs) {
        $candidate = Join-Path $lib "steamapps\common\TCG Card Shop Simulator"
        if (Test-Path "$candidate\Card Shop Simulator.exe") { $game = $candidate; break }
    }
}
if (-not $game) {
    Write-Host "TCG Card Shop Simulator not found. Install it in Steam, run it once, then re-run this." -ForegroundColor Red
    pause; exit 1
}
Write-Host "Game found: $game" -ForegroundColor Green

# --- copy mod layer ---
Write-Host "Copying mods (~57 GB, be patient)..." -ForegroundColor Cyan
robocopy "$src\BepInEx" "$game\BepInEx" /E /NFL /NDL /NP
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "steam_appid.txt") {
    if (Test-Path "$src\$f") { Copy-Item "$src\$f" $game -Force }
}

# --- desktop shortcuts ---
$dadIp = (Get-Content "$src\dad-ip.txt" -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $dadIp) { $dadIp = Read-Host "Enter dad's PC IP address (shown in his co-op window when hosting)" }

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut("$([Environment]::GetFolderPath('Desktop'))\Play with Dad.lnk")
$lnk.TargetPath = "$game\Card Shop Simulator.exe"
$lnk.Arguments = "-coopautojoin=$dadIp"
$lnk.WorkingDirectory = $game
$lnk.Description = "Launches the game and automatically joins dad's card shop"
$lnk.Save()

Write-Host @"

All set!

  1. Make sure Steam is running and this PC's account owns the game.
  2. DAD first: load the shop, press F11, click 'Host my shop'.
  3. SON: double-click 'Play with Dad' on the desktop. It joins and loads
     dad's shop automatically (takes a minute or two with all the mods).

  If dad's IP ever changes, right-click the shortcut > Properties and fix
  the number after -coopautojoin=
"@ -ForegroundColor Green
pause
