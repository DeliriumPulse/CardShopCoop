# Nexus page content — copy-paste kit

## Name
CardShopCoop - True Co-op Multiplayer

## Summary (short description, <250 chars)
True co-op for TCG Card Shop Simulator: run one shop together over Steam or LAN. Shared money, XP, collection, customers, register, trades, everything. Works with PTCGO/EPL content mods, auto-syncs card databases. 2+ players.

## Category
Gameplay (or Multiplayer if available)

## Description (BBCode — paste into the Nexus description editor)

[size=5][b]Run the card shop together.[/b][/size]

CardShopCoop turns TCG Card Shop Simulator into a real co-op game: one shared shop, one shared wallet, one shared collection — with both players stocking shelves, serving the register, haggling with trade-in customers, opening packs, and watching the same crowd walk the same aisles.

Built and battle-tested by a dad playing with his son across the room and his brother across town.

[size=4][b]Features[/b][/size]
[list]
[*][b]Steam lobbies[/b] — invite from your friends list, or host a public lobby (searchable browser, optional password). LAN/direct IP also supported.
[*][b]Everything is shared[/b] — money, shop XP/level/fame, the card collection (graded cards included), item and card prices, shelf stock, card display walls, loose delivery boxes, furniture, licenses, bills, shop expansions, decorations, the OPEN/CLOSED sign, tournaments, card grading, the daily market, and the end-of-day report.
[*][b]Both players actually work[/b] — the joiner can serve the register (scan by clicking items or hold V), run customer trade-ins and sell-ins through the game's real trade screen (haggling included), restock, set prices, order stock and furniture from the phone, hire staff, pay bills, and buy licenses. Every action executes in the host's real simulation.
[*][b]Smooth[/b] — customers and the other player move on interpolated motion (no teleport-jitter), with real carried items in their hands: product boxes, card fans, binders.
[*][b]Content-mod friendly[/b] — made for the PTCGO / Enhanced Prefab Loader ecosystem. The join handshake verifies both players' mod sets and card-ID registries with readable messages, [b]auto-syncs the card database[/b] (backs up yours, installs the host's, asks for one restart), warns you in plain language when product catalogs differ, and refunds orders for products the host doesn't have.
[*][b]Joiners risk nothing[/b] — the joiner downloads the host's save at join and plays in a dedicated scratch slot. Your own solo saves are never touched, never overwritten, never saved over.
[/list]

[size=4][b]Requirements[/b][/size]
[list]
[*]BepInEx 5 (5.4.23 x64) installed in the game folder on both PCs
[*]The [b]same CardShopCoop version on both PCs[/b]
[*]The same mod list on both PCs, including content packs (the mod tells you exactly what differs if not)
[/list]

[size=4][b]Installation[/b][/size]
[list=1]
[*]Install BepInEx 5 if you don't have it (most modded installs already do).
[*]Drop [font=Courier New]CardShopCoop.dll[/font] into [font=Courier New]BepInEx/plugins/[/font] on both PCs.
[*]Start the game and press [b]F2[/b] for the co-op window.
[/list]

[size=4][b]Quick start[/b][/size]
[b]Host:[/b] load your save normally, press F2, choose Steam (public or friends-only, optional password) or LAN, click Host. Keep playing.
[b]Friend:[/b] press F2, join via Steam invite, the lobby browser, or the host's LAN IP. The shop downloads in ~30-60 seconds and you appear in the host's store.

If your modded card database differs from the host's, the mod syncs it automatically (your original is backed up beside it) and asks you to restart and rejoin — one restart, done.

[size=4][b]Good to know / limitations[/b][/size]
[list]
[*]The host's save is the world. The joiner is a guest: their own saves are never written while visiting.
[*]Designed and heavily tested for 2 players; additional joiners are relayed by the host (lightly tested).
[*]Staff management beyond hiring (fire/tasks/bonuses) is host-only — the game offers no joiner-side path to those screens.
[*]Steam achievements progress per-player.
[*]The joiner can't sit down for the play-table minigame (needs the live simulation); table layouts are mirrored visually.
[*]Content packs installed mid-save price themselves on the host — if modded items show $0, the host can press F11 (PTCGO Economics price reset) and correct values sync over.
[/list]

[size=4][b]Troubleshooting[/b][/size]
[list]
[*][b]"your mod set differs"[/b] — one of you is missing a plugin or runs a different version. Mirror your BepInEx/plugins folders.
[*][b]"your custom-card database differed - it has been synced"[/b] — working as intended: restart the game and join again.
[*][b]"product catalogs differ"[/b] — you have different content DATA packs (these aren't plugins, so the mod-set check can't see them). Mirror your content pack folders — including the small .json files next to the big bundle files.
[*]Anything else: the mod writes a detailed log to [font=Courier New]BepInEx/CardShopCoop_(number).log[/font] on both PCs — include both logs in a bug report.
[/list]

[size=4][b]Source[/b][/size]
MIT-licensed source on GitHub: [i](add repo link here)[/i]

## Permissions (Nexus form suggestions)
- Others can convert/modify with credit: yes
- Upload to other sites: no (link back here)
- Asset use: n/a (no game assets included)

## Changelog seed
1.0.0 — Initial public release. Steam lobbies + LAN, full shared-shop sync (economy, collection, stock, prices, boxes, furniture, licenses, bills, expansions, tournaments, grading, market, reports), joiner register + trade serving via the native screens, interpolated motion, PTCGO/EPL parity checks with automatic card-database sync and catalog diagnostics.
