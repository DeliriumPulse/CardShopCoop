# CardShopCoop — Play Guide

Run your Pokemon card shop **together**. One of you hosts (the real shop), the other
joins and plays inside it. Built for your exact mod setup (PTCGO + expansions + EPL);
it never touches card content, so the Pokemon mods just work.

## What you need

- Two PCs on the same home network.
- Two Steam accounts that each own TCG Card Shop Simulator, one logged in per PC.
  (Steam Family Sharing does **not** allow playing the same game at the same time.)
- The same mods on both PCs — the setup scripts handle that by mirroring dad's
  `BepInEx` folder.

## One-time setup (son's PC)

1. Install TCG Card Shop Simulator from Steam, launch it once to the title screen, quit.
2. On dad's PC: run `Stage-ModsToUSB.ps1` (right-click → Run with PowerShell), point it
   at a USB drive with ~60 GB free.
3. Plug the USB into the son's PC and run `Setup-SonPC.ps1` from it.
   It copies the mods and puts a **"Play with Dad"** shortcut on the desktop.

## Playing (every session)

### The easy way — Steam invites (works from anywhere, no IPs)

| Who | What to do |
|-----|------------|
| **Host** | Load your shop, press **F2**, click **Host via Steam**, then **Invite friend** — pick them in the Steam overlay. |
| **Joiner** | Accept the invite from Steam (game running at the main menu, or not running at all — Steam will launch it). The shop downloads and loads automatically. |

Both players need to be Steam friends, and both PCs need the same mods (the USB setup).

### The LAN way (same house, no Steam friends needed)

| Who | What to do |
|-----|------------|
| **Host** | Load your shop, press **F2**, click **Host via LAN**. The window shows your IP. |
| **Joiner** | Double-click **Play with Dad** (auto-joins), or press **F2** at the main menu, type the host's IP, click **Join LAN**. |

First time hosting via LAN, Windows Firewall will ask — click **Allow** (private networks).

## Controls

- **F2** — open/close the co-op window
- **G** — wave emote (pops above your head on the other screen)

## What syncs right now (v0.2)

- ✅ You see each other walking around (customer-style avatar with name tag)
- ✅ Shelf and warehouse stock — restocking on either side shows up on the other within a second
- ✅ Money (host's wallet is THE wallet), day number, time of day
- ✅ Item prices, including modded pricing data (transferred when joining)
- ✅ The whole shop layout, licenses, expansions — the joiner literally plays a copy of dad's save

## Current limitations (good to know, nothing breaks)

- Customers and workers only exist on the **host's** screen. The joiner sees a calm
  shop and helps by restocking, organizing, opening boxes. (Customer mirroring is the
  next planned feature.)
- The joiner should **not** buy warehouse restock orders — the host should make
  purchases so the shared wallet stays honest.
- Card-pack opening and the tabletop game work for each player, but the other player
  doesn't see the animation.
- The joiner can't end the day — days advance when the host sleeps.
- If the connection drops, the joiner keeps a frozen copy of the world and their own
  saves are untouched (everything co-op lives in save slot 7).

## Safety

- The joiner's real saves (slots 0–3) are **never** written to — a patch reroutes all
  co-op saving into slot 7.
- Dad's save is only read and re-saved through the game's own save system, same as
  single player. (A backup of all saves from before the first co-op session lives at
  `C:\Users\zwhit\CardShopCoop\backup\`.)

## Troubleshooting

- **"Could not connect"** — check the IP (dad's co-op window shows it), both on same
  network, firewall allowed on dad's PC.
- **Joined but kicked with version mismatch** — both PCs must run the same CardShopCoop
  version; re-run the USB setup after dad updates mods.
- **Cards/items look wrong on the joiner** — mods differ between the PCs; re-mirror
  with the USB scripts. Never update mods on one PC only.
- **Logs** (send these to Claude when something's weird):
  `...\TCG Card Shop Simulator\BepInEx\CardShopCoop_<number>.log` on both PCs.

## After a game update

Game patches can break mods (all of them, not just this one). If the game updates and
things break: on dad's PC re-check the mod set, rebuild CardShopCoop
(`dotnet build -c Release` in `C:\Users\zwhit\CardShopCoop\src\CardShopCoop`), then
re-run the USB mirror. Claude can re-verify the decompiled hooks if a patch changes them.
