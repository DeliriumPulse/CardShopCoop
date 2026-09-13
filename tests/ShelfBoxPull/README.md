# Shelf-to-box transfer regression checks

Run from the repository root with the .NET 9 SDK:

```powershell
dotnet run --project tests/ShelfBoxPull/ShelfBoxPull.csproj -c Release
```

The harness compiles the production `ShelfBoxPull` request executor directly and substitutes
inventory-only game objects for Unity rendering/pooling. It checks the issue #9 stale-count
scenario, competing last-item requests, duplicates, rejected requests after refill, full or
wrong-type destinations, empty retained labels, and reconnect sequence cleanup.

These are deterministic logic checks, not a multiplayer gameplay or rendering test.
The production Harmony prefix skips the guest's `RemoveItemFromShelf` entirely; WorldSync
resolves the host's shelf and owned box, invokes this executor, and publishes host contents.
Host and single-player actions continue through vanilla. No optimistic guest item is created.
