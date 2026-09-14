# Contributing to CardShopCoop

You can help by reporting bugs, testing with another player, improving the docs,
reviewing changes, or contributing code. For a larger change, open an issue first
so we can discuss the approach.

## Contributor credit

[Meepen](https://github.com/meepen) has contributed continued development, fixes,
and testing, including most of the 1.2.0 update. His credit is also in the
[README](README.md#contributors).

Preserve authorship when building on someone else's work. Name contributors,
reporters, and testers in the pull request where relevant, and carry those credits
into release notes when their work is included. Link to the original issue or
pull request so people can follow the contribution. Use the name or handle the
person wants credited; ask if you are unsure.

## Reporting a bug

[Open an issue](https://github.com/DeliriumPulse/CardShopCoop/issues) with:

- Game and CardShopCoop versions, plus the other mods installed on each player’s PC.
- Whether the problem happened on the host or a joining player, using Steam or LAN.
- Steps to reproduce it, what you expected, and what actually happened.
- Relevant log excerpts, screenshots, or a short video if they help.

Remove personal information and connection details from logs before posting them.
For multiplayer problems, logs from both players help trace where their state
stopped matching.

## Development setup

Use the .NET 9 SDK and a local installation of TCG Card Shop Simulator with
BepInEx 5. The plugin needs the game’s managed assemblies to compile; those files
are not distributed in this repository.

1. Fork this repository and clone your fork.
2. Copy `Directory.Build.user.props.example` to `Directory.Build.user.props` and
   set your game path. This file is ignored by Git. You can also set
   `CARDSHOP_GAMEPATH` or pass `-p:GamePath=...` for a single build.
3. Build from the repository root:

```sh
dotnet build src/CardShopCoop/CardShopCoop.csproj -c Release
```

The build does not deploy by default. Add `-p:Deploy=true` only when you want to
copy the DLL into your game’s plugins directory, with the game closed.
Do not put your local game path in a committed project file.

Read [AGENTS.md](AGENTS.md) for the project’s architecture, versioning rules, and
instructions for inspecting the game’s code locally. Keep game assemblies,
decompiled game source, saves, and local build output out of your contribution.

## Checks before a pull request

Run the same formatting check as CI from the repository root:

```sh
dotnet restore src/CardShopCoop/CardShopCoop.csproj --locked-mode --configfile NuGet.Config
dotnet format src/CardShopCoop/CardShopCoop.csproj whitespace --verify-no-changes --no-restore
```

If formatting fails, run the format command without `--verify-no-changes`, inspect
the changes, and run the check again.

For shelf-to-box transfer changes, run the existing regression checks:

```sh
dotnet run --project tests/ShelfBoxPull/ShelfBoxPull.csproj -c Release
```

These checks exercise transfer logic with substitute game objects. They do not
replace multiplayer gameplay testing. See the [test README](tests/ShelfBoxPull/README.md)
for their scope.

For gameplay or networking changes, test with a host and a joining player on the
same plugin version and mod set. Check the affected action from both sides and
after reconnecting. Use a backed-up or disposable save. Describe what you tested
and anything you could not test. CI currently checks formatting, not a full build
against the game or an in-game multiplayer session.

## Pull requests

Keep each pull request focused on one problem. Explain the player-visible issue,
what changed, and how you checked it. Include reproduction steps for bug fixes
and credit anyone whose work or report helped.

Preserve host authority and use stable game identities in network messages.
Follow the versioning rules in [AGENTS.md](AGENTS.md): `CardShopCoopVersion` in
`Directory.Build.props` is the single version source. Call out wire-format or
message-behavior changes explicitly so compatibility can be reviewed.

Release notes belong in [CHANGELOG.md](CHANGELOG.md). Do not add a separate
release-notes directory or duplicate the release record.

AI-assisted contributions are welcome. Read and understand the code you submit,
check it against the actual game behavior, and report the tests you ran. If you
could not build or test a change, say so in the pull request.

## License

CardShopCoop uses the [MIT license](LICENSE). Preserve existing copyright and
license notices when reusing code.
