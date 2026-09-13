using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Everything in this mod syncs by game IDs, which is only safe when both players run
    /// the SAME mod set (content mods define the ID space). These hashes are exchanged in
    /// the Hello handshake; mismatches are rejected with a readable reason instead of
    /// silently corrupting the shared world.
    /// </summary>
    public static class ModParity
    {
        private static string _plugins;
        private static string _enum;
        private static string _cards;

        /// <summary>Set ONLY by InstallEnumFile: the HOST's registry now sits on disk in place of
        /// ours. The ids this process is running are still our own (EPL loaded them at prepatch,
        /// long before the write), so re-Helloing now would hand the host the very lines it just
        /// rejected and earn the same rejection again - the endless "synced - RESTART - rejoin"
        /// loop. CoopCore reads this by name to refuse a join with a "restart first" reason
        /// instead of burning a whole handshake on it. Never cleared: only a new process can
        /// load the file that was just installed.</summary>
        public static bool RestartRequiredForJoin;

        /// <summary>Set ONLY by RestoreEnumBackup: the player's OWN registry is back on disk, but
        /// this process is still running whatever ids it booted with, so a SOLO save that needs
        /// the restored registry wants a restart first. Deliberately NOT a join gate - restoring
        /// a file changes nothing about the ids we are running, so it must not cost the player a
        /// second restart before they can accept dad's invite. Informational: the restore
        /// message the UI shows already says it, this is the flag form of the same fact.</summary>
        public static bool RestartRequiredForSolo;

        /// <summary>True when enum_values.json on disk is still the registry THIS PROCESS
        /// actually loaded - i.e. when handing our file to somebody else is honest.
        ///
        /// EPL reads the registry once, at prepatch. From then on the only things that can make
        /// the bytes on disk disagree with the ids we are running are our own two writes, and
        /// both are already tracked exactly: InstallEnumFile raises RestartRequiredForJoin (the
        /// HOST's file is now sitting where ours was) and RestoreEnumBackup raises
        /// RestartRequiredForSolo (our own backup is back). Neither is ever cleared, because
        /// only a new process can load what was just written. So "no write happened in this
        /// process" IS "the file describes what I am running".
        ///
        /// THE ONE EXCEPTION, and why it is not an exception at all: install-then-restore inside
        /// ONE process. If InstallEnumFile wrote our own file aside as a backup and RestoreEnumBackup
        /// then put THAT VERY backup back, the bytes on disk are provably the bytes this process
        /// booted with - two writes that cancel. Both flags are cleared in that case (see
        /// _installBackupPath), so this correctly reports true again instead of gating a host out
        /// of auto-syncing anyone for the rest of the session over a change that was undone.
        ///
        /// This deliberately does NOT ask HostEnumInstalled(). That marker answers a different
        /// question - "is the registry on disk borrowed from some other host?" - and using it
        /// here is what made a player who had ONCE joined somebody unable to auto-sync anyone
        /// ever afterwards: the marker survives restarts, so a host who had restarted (and was
        /// therefore genuinely RUNNING the borrowed registry, making it a perfectly good thing
        /// to ship) still hard-rejected every conflicting guest AND skipped sending the file
        /// that would have fixed them. A borrowed-but-loaded registry is a real, coherent id
        /// space; it is only the UNLOADED one that must not be shipped.</summary>
        public static bool RegistryFileMatchesRuntime()
        {
            return !RestartRequiredForJoin && !RestartRequiredForSolo;
        }

        /// <summary>Hash of the custom-card ID space minted by CreateCards: each
        /// MonsterConfig's "Monster Type = Monster Type ID" mapping, sorted. This is
        /// exactly what WriteCard/ReadCard sync depends on. The plugin and enum hashes
        /// do NOT cover it - custom EMonsterType cards aren't in EPL's enum_values.json -
        /// so without this, two players could pass the handshake and then silently show
        /// the wrong card. Cosmetic fields (art, stats, description) are excluded on
        /// purpose so a shared card with tweaked flavor still matches.</summary>
        public static string CardsHash()
        {
            if (_cards != null)
                return _cards;
            try
            {
                var entries = CardEntries();
                _cards = entries.Count == 0 ? "none" : Short(Sha1(string.Join(";", entries)));
            }
            catch { _cards = "err"; }
            return _cards;
        }

        /// <summary>The exact sorted "Monster Type=Monster Type ID" identity strings CardsHash
        /// hashes, exposed as a list so the handshake can SHOW the mismatch, not just reject it.
        /// Both come from CardEntries() so the list a player sees can never disagree with the
        /// hash that gated them. CoopCore calls this by name.</summary>
        public static List<string> CardsList()
        {
            try
            {
                return CardEntries();
            }
            catch (System.Exception e) { Swallow.Log(e); return new List<string>(); }
        }

        /// <summary>The .ini-derived custom-card identity strings, sorted - the single source
        /// both CardsHash and CardsList read so hash and list are always in step.</summary>
        private static List<string> CardEntries()
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "patchers", "CreateCardsPreloader", "MonsterConfigs");
            var entries = new List<string>();
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*.ini"))
                {
                    string name = null, id = null;
                    foreach (var line in File.ReadAllLines(f))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0)
                            continue;
                        string k = line.Substring(0, eq).Trim();
                        if (k.Equals("Monster Type", StringComparison.OrdinalIgnoreCase))
                            name = line.Substring(eq + 1).Trim();
                        else if (k.Equals("Monster Type ID", StringComparison.OrdinalIgnoreCase))
                            id = line.Substring(eq + 1).Trim();
                    }
                    if (name != null && id != null)
                        entries.Add(name + "=" + id);
                }
            }
            entries.Sort(StringComparer.Ordinal);
            return entries;
        }

        /// <summary>Hash of the loaded BepInEx plugin set (guid=version, sorted).</summary>
        public static string PluginHash()
        {
            if (_plugins != null)
                return _plugins;
            try
            {
                _plugins = Short(Sha1(string.Join(";", PluginEntries())));
            }
            catch { _plugins = "err"; }
            return _plugins;
        }

        /// <summary>The same sorted "guid=version" entries PluginHash hashes, exposed as a list
        /// so a mismatch can be shown side-by-side instead of just rejected. Shares PluginEntries()
        /// with PluginHash so the two never disagree. CoopCore calls this by name.</summary>
        public static List<string> PluginList()
        {
            try
            {
                return PluginEntries();
            }
            catch (System.Exception e) { Swallow.Log(e); return new List<string>(); }
        }

        /// <summary>The loaded plugin set as sorted "guid=version" strings - the single source
        /// both PluginHash and PluginList read.</summary>
        private static List<string> PluginEntries()
        {
            var parts = new List<string>();
            foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                parts.Add(kv.Key + "=" + kv.Value.Metadata.Version);
            parts.Sort(StringComparer.Ordinal);
            return parts;
        }

        /// <summary>EPL's custom-item ID registry on disk. Nothing about our IDENTITY reads this
        /// file while the running enums can answer instead (EnumHash/EnumLines walk the enums THIS
        /// PROCESS actually loaded) - it survives as the payload we ship to a guest and write over
        /// theirs, and as a last-resort fallback when the walk comes up empty on an EPL machine.
        ///
        /// WHAT EPL ACTUALLY DOES (read off EnhancedPrefabLoader 6.0.0's prepatcher; the comment
        /// that used to sit here claimed the opposite and that mistake is what produced the
        /// 1.0.35 bugs). At PREPATCH, EPL LOADS this file and re-creates every name in it with
        /// the EXACT id saved against it - including names whose content is NOT installed on this
        /// PC. Only names absent from the file get a fresh id, minted sequentially from
        /// ModdedIdFloor upward in unsorted bundle-discovery order. Therefore:
        ///  (a) Installing the host's registry and RESTARTING DOES converge. The guest's map
        ///      becomes a superset of the host's, with identical ids for every shared name. It is
        ///      NOT true that "the game rebuilds it from your own content packs so copying the
        ///      host's file cannot help" - that was the false invariant. The everyday cause of a
        ///      mismatch is the same packs installed in a different ORDER, which is nothing but a
        ///      permutation of one id set (the constant "+6 offset across non-contiguous ids" in
        ///      the field reports).
        ///  (b) The file still stops describing the RUNNING game the instant anyone writes to it,
        ///      because our ids were read at prepatch, long before the write. So a write here MUST
        ///      NOT change our hash mid-session; only a RESTART loads the new ids. That is exactly
        ///      what the 1.0.33 "_enum = null" invalidations broke - they let an unrestarted guest
        ///      re-Hello on the strength of bytes the running game had never read, quietly
        ///      defeating the documented restart requirement. Both are gone; see
        ///      RestartRequiredForJoin.
        ///  (c) The path is machine-global and lives under LocalLow, which NO uninstall touches.
        ///      A registry (and our .hostlend marker) can therefore outlive EPL itself, so
        ///      anything that reads either of them must first ask EplLoaded().</summary>
        public static string EnumFilePath()
        {
            return Path.Combine(Application.persistentDataPath, "PrefabLoader", "enum_values.json");
        }

        /// <summary>Type names that exist only when EnhancedPrefabLoader is actually LOADED in
        /// this process. Deliberately the SAME strings the rest of the mod already probes for
        /// (CoopCore's catalog bridge and Sync/MarketSync's EPL bridge both resolve
        /// EplRuntimeData; MarketSync also resolves ItemSaveData) so there is one set of names to
        /// keep in step with an EPL update rather than a private one invented here. ANY hit
        /// counts as loaded - EPL would have to move or rename every one of them at once before
        /// the probe goes dark, and a dark probe fails toward "vanilla", which is the safe side
        /// (an empty modded set conflicts with nobody).</summary>
        private static readonly string[] EplSentinelTypeNames =
        {
            "EnhancedPrefabLoader.Core.EplRuntimeData",
            "EnhancedPrefabLoader.Core.Models.SaveData.ItemSaveData"
        };

        /// <summary>Simple name of the assembly the sentinels above live in - the second half of
        /// the assembly-qualified name EplLoaded asks Type.GetType for. Kept beside the sentinels
        /// so an EPL rename moves both together.</summary>
        private const string EplAssemblyName = "EnhancedPrefabLoader";

        private static bool _eplLoaded;
        private static bool _eplProbed;
        private static int _eplProbeTick;
        private static bool _eplLoadedLogged;

        /// <summary>True when EnhancedPrefabLoader is loaded in THIS process, i.e. when a custom
        /// id registry is actually in play. Everything that reads enum_values.json (or the
        /// .hostlend marker beside it) has to ask this FIRST: both live under LocalLow and
        /// nothing removes them when EPL is uninstalled, so a plain vanilla game still has a full
        /// modded registry - and possibly a borrow marker - sitting on disk. Trusting those made
        /// a vanilla process report a modded identity and get rejected by another vanilla
        /// process, and made a vanilla host permanently refuse guests it should have synced.
        ///
        /// Cached, but only a POSITIVE result is permanent. This is reachable from OnGUI (the
        /// restore prompt polls HostEnumInstalled every frame) and the earliest such call can
        /// land before BepInEx has chainloaded EPL's plugin assembly, so latching the first "no"
        /// forever would be its own stale-latch bug. A "no" is therefore re-checked, throttled -
        /// it is a probe, not a per-frame cost we want to pay on a vanilla machine.
        ///
        /// 1.0.38 - WHY THERE IS NO APP-DOMAIN TYPE WALK HERE ANY MORE. This used to call
        /// AccessTools.TypeByName, which enumerates the types of EVERY loaded assembly. Ours is
        /// one of them, and on Game Pass our DLL intentionally contains types that cannot load
        /// (everything Steam-typed - see Net/ISteamBridge). The walk hits them, HarmonyX logs a
        /// ReflectionTypeLoadException naming Steamworks types, and a player reads that as the
        /// mod crashing when it is doing exactly what it was designed to do - and the probe runs
        /// every 5 seconds on a vanilla machine, so it is not one line but a stream of them.
        /// Both branches below ask a targeted question instead and are quiet on every build:
        ///  (a) Type.GetType("&lt;sentinel&gt;, EnhancedPrefabLoader") - a direct, assembly-qualified
        ///      bind. It loads nothing that is not already loaded, touches no other assembly, and
        ///      returns null quietly when EPL is absent (throwOnError:false).
        ///  (b) BepInEx's own plugin list - metadata strings only, no reflection at all - for the
        ///      case where EPL's assembly simple name is not what we assumed. Matching on
        ///      "enhancedprefabloader" in the GUID or the name is deliberately loose: this is the
        ///      backstop, and a false NEGATIVE here costs the modded machine its ID-conflict gate.</summary>
        public static bool EplLoaded()
        {
            if (_eplLoaded)
                return true;
            int now = Environment.TickCount;
            // unchecked int subtraction compares correctly across TickCount's ~24.9-day wrap
            if (_eplProbed && unchecked(now - _eplProbeTick) < 5000)
                return false;
            _eplProbed = true;
            _eplProbeTick = now;
            foreach (var typeName in EplSentinelTypeNames)
            {
                try
                {
                    if (Type.GetType(typeName + ", " + EplAssemblyName, false) == null)
                        continue;
                    _eplLoaded = true;
                    break;
                }
                catch (System.Exception e) { Swallow.Log(e); /* a probe must never throw into a handshake or into OnGUI */ }
            }
            if (!_eplLoaded)
            {
                try
                {
                    foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                    {
                        var meta = kv.Value != null ? kv.Value.Metadata : null;
                        if (meta == null)
                            continue;
                        if (Mentions(kv.Key, "enhancedprefabloader") || Mentions(meta.Name, "enhancedprefabloader"))
                        {
                            _eplLoaded = true;
                            break;
                        }
                    }
                }
                catch (System.Exception e) { Swallow.Log(e); /* same rule: a probe must never throw */ }
            }
            if (_eplLoaded && !_eplLoadedLogged)
            {
                _eplLoadedLogged = true;
                try
                {
                    CoopPlugin.Log.LogInfo("EnhancedPrefabLoader detected - a custom id registry is in play");
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            return _eplLoaded;
        }

        /// <summary>THE ONE WAY THIS MOD LOOKS UP ANOTHER MOD'S TYPE. Asks for the type by its
        /// assembly-qualified name first - a direct bind that loads nothing new, touches no
        /// other assembly and answers null quietly - and only falls back to
        /// AccessTools.TypeByName, which walks the types of EVERY loaded assembly, when that
        /// misses (a mod repackaged under a different assembly name, or a name we guessed
        /// wrong).
        ///
        /// The walk is the thing worth avoiding: OUR assembly is one of the ones it enumerates,
        /// and on the Game Pass build our DLL deliberately contains types that cannot load
        /// (everything Steam-typed - see Net/ISteamBridge). Every walk therefore makes HarmonyX
        /// log a ReflectionTypeLoadException naming Steamworks types, which reads to a player
        /// as the mod crashing while it is doing exactly what it was designed to do. Same fast
        /// path EplLoaded uses; this is the version the rest of the mod shares.
        ///
        /// <paramref name="assemblySimpleName"/> is the SIMPLE name of the assembly the type
        /// lives in (no version, no key) - "EnhancedPrefabLoader", "Grading Overhaul".</summary>
        public static Type ResolveType(string typeName, string assemblySimpleName)
        {
            try
            {
                Type t = Type.GetType(typeName + ", " + assemblySimpleName, false);
                if (t != null)
                    return t;
            }
            catch (System.Exception e) { Swallow.Log(e); /* a lookup must never throw into a handshake, a probe or OnGUI */ }
            try
            {
                return HarmonyLib.AccessTools.TypeByName(typeName);
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        /// <summary>Case-insensitive substring test used by the plugin-list backstop above.
        /// Null-safe: BepInEx metadata fields are plain strings and nothing guarantees them.</summary>
        private static bool Mentions(string haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The one wording for "this process loads no registry at all", shared by
        /// EnumHash and EnumLines so the log can never claim one thing while the handshake does
        /// another.</summary>
        private const string VanillaEnumNote =
            "EPL not loaded - vanilla, modded set is EMPTY; any enum_values.json on disk is ignored";

        /// <summary>The enum types EPL mints custom ids into - exactly the six sections a live
        /// enum_values.json carries, all global-namespace enums in the decompiled Assembly-CSharp.
        /// Resolved BY NAME at runtime (the mod's usual AccessTools idiom) so a game update that
        /// renames or drops one costs us that one section instead of throwing a TypeLoadException
        /// straight through the handshake.</summary>
        private static readonly string[] ModdedEnumTypeNames =
        {
            "EObjectType", "EDecoObject", "EItemType",
            "ECardExpansionType", "ERarity", "ECollectionPackType"
        };

        /// <summary>First id EPL hands out. Vanilla members are dense 0..~135 per enum apart from
        /// a `None = -1` sentinel (ECardExpansionType and ERarity both declare one), and -1 is
        /// below the floor like everything else vanilla, so everything from here up is modded
        /// content - the only slice of the ID space that can differ between two players.</summary>
        private const long ModdedIdFloor = 200000;

        /// <summary>Hash of the modded ID space THIS PROCESS is actually running. We ask the
        /// loaded enum types what ids they hold rather than reading enum_values.json, because the
        /// file is not the truth about the RUNNING game: EPL reads it once at prepatch, so from
        /// that moment on the bytes on disk can be anything (see EnumFilePath). The runtime walk
        /// describes what our game will really do with an incoming ID - and it cannot be faked by
        /// dropping a matching file in place, because nothing short of a restart changes it.
        /// Falls back to the canonical FILE parse only when the walk finds nothing modded at all
        /// AND EPL is genuinely loaded here, and ends at "none" on a vanilla machine: the Hello
        /// check treats "none" as "unmodded, don't gate on it".</summary>
        public static string EnumHash()
        {
            if (_enum != null)
                return _enum;
            try
            {
                var lines = RuntimeEnumEntries();
                if (lines.Count > 0)
                {
                    _enum = Short(Sha1(string.Join("\n", lines)));
                    return _enum;
                }
                // 1.0.36 STALE-FILE GUARD - the same one EnumLines carries, and for the same
                // reason. Zero modded ids in memory has two very different causes and the file
                // fallback is only correct for ONE of them. If EPL is not loaded, this is a
                // vanilla process: its modded set is genuinely EMPTY, and the enum_values.json
                // that may still be sitting in LocalLow from an old modded install describes
                // nothing this process will ever run. Hashing it made a clean vanilla game
                // announce a full modded identity and get rejected against another clean vanilla
                // game, and printed a fictitious hash in the log beside the rejection. Say
                // "none" instead - honest, and the Hello check reads it as "don't gate".
                if (!EplLoaded())
                {
                    LogEnumSourceOnce(VanillaEnumNote);
                    _enum = "none";
                    return _enum;
                }
                // EPL IS loaded but the walk saw nothing: a renamed enum type, a throwing
                // reflection call, ids minted somewhere Enum.GetNames can't see. That is the
                // fallback's real purpose, so keep the pre-existing file behavior here - this
                // must never be WORSE than 1.0.33. Hash the registry's MEANING, not its bytes:
                // EPL re-serializes the file (ordering / whitespace churn), and a byte hash made
                // two LOGICALLY IDENTICAL registries mismatch forever - the endless "synced -
                // RESTART - rejoin" loop (field report: "custom card database error, regardless
                // of multiple restarts, via LAN"). Byte hash remains the last resort if the
                // format ever changes under the parser.
                string p = EnumFilePath();
                if (!File.Exists(p))
                {
                    _enum = "none";
                    return _enum;
                }
                var fileLines = CanonicalEnumLines(File.ReadAllText(p));
                _enum = fileLines != null && fileLines.Count > 0
                    ? Short(Sha1(string.Join("\n", fileLines)))
                    : Short(Sha1Bytes(File.ReadAllBytes(p)));
            }
            catch { _enum = "none"; }
            return _enum;
        }

        /// <summary>The exact sorted "EnumType:Name=id" lines EnumHash hashes, exposed as a list
        /// so a mismatch can be SHOWN (which custom item, whose id) instead of only rejected -
        /// the same pairing PluginHash/PluginList and CardsHash/CardsList already use. These
        /// lines are ALSO the whole ID-conflict gate (CoopCore.EnumConflicts short-circuits to
        /// "no conflicts" the moment either side is empty), so they get the SAME fallback chain
        /// EnumHash has: runtime walk first, then the canonical FILE parse filtered to modded
        /// ids. Without that, a modded machine whose walk came up empty - EPL minting ids in a
        /// way Enum.GetNames can't see, a renamed enum type, a throwing reflection call - would
        /// hand the host an empty blob and silently disable the gate for that join while
        /// EnumHash still printed a plausible file-derived hash in the log beside it. (On that
        /// fallback the two are no longer the same set - EnumHash covers the whole file, these
        /// lines only the modded slice - which is fine: the hash gates nothing any more, it is
        /// a log diagnostic.) That fallback is gated on EPL actually being loaded here (1.0.36);
        /// empty is the honest - and only correct - answer on a vanilla machine: nothing modded,
        /// conflicts with nobody. CoopCore calls this by name.</summary>
        public static List<string> EnumLines()
        {
            try
            {
                var lines = RuntimeEnumEntries();
                if (lines.Count > 0)
                {
                    LogEnumSourceOnce("runtime enums (" + lines.Count + " modded ids)");
                    return lines;
                }

                // 1.0.36 STALE-FILE GUARD. enum_values.json lives in LocalLow and NO uninstall
                // clears it, so a game with EPL removed - or one that never had it - can still
                // find a fat modded registry on disk. Falling through to the file there made a
                // VANILLA process hand the host a full set of modded lines, which then collided
                // with the other vanilla player's equally stale file and hard-rejected two
                // clean installs from playing together. A process that loads no registry has an
                // EMPTY modded set, full stop; the bytes on disk are somebody's leftovers.
                if (!EplLoaded())
                {
                    LogEnumSourceOnce(VanillaEnumNote);
                    return lines;
                }

                // Past here EPL really is loaded and the walk still found nothing - the case the
                // file fallback exists for (renamed enum type, throwing reflection call, ids
                // minted where Enum.GetNames can't see them).
                string p = EnumFilePath();
                if (!File.Exists(p))
                {
                    LogEnumSourceOnce("EPL is loaded but no modded ids were found and there is no registry file");
                    return lines;
                }
                var fileLines = CanonicalEnumLines(File.ReadAllText(p));
                if (fileLines == null)
                {
                    LogEnumSourceOnce("registry file unparseable - ID-conflict check disabled");
                    return lines;
                }
                // The file carries the FULL id space (vanilla members included); only the
                // modded slice can differ between two players, so filter it exactly the way
                // RuntimeEnumEntries does. CanonicalEnumLines already sorted, and dropping
                // entries keeps that order, so no re-sort is needed.
                var modded = new List<string>();
                foreach (var line in fileLines)
                {
                    int eq = line.LastIndexOf('=');
                    if (eq <= 0 || eq == line.Length - 1)
                        continue;
                    long id;
                    if (!long.TryParse(line.Substring(eq + 1), out id))
                        continue;
                    if (id < ModdedIdFloor)
                        continue;
                    modded.Add(line);
                }
                LogEnumSourceOnce("enum_values.json fallback (" + modded.Count + " modded ids) - the runtime walk found none");
                return modded;
            }
            catch (System.Exception e) { Swallow.Log(e); return new List<string>(); }
        }

        private static bool _enumSourceLogged;

        /// <summary>Say ONCE per session where our registry lines came from. Which source
        /// answered decides whether the join-time ID-conflict gate is live or quietly
        /// short-circuited, so it belongs in the log next to the hashes - but it is read on
        /// every Hello, and one line per join attempt would be noise.</summary>
        private static void LogEnumSourceOnce(string source)
        {
            if (_enumSourceLogged)
                return;
            _enumSourceLogged = true;
            try
            {
                CoopPlugin.Log.LogInfo("enum identity source: " + source);
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        /// <summary>The modded enum ids resolved from the LOADED types, sorted - the single source
        /// both EnumHash and EnumLines read. EPL injects its minted members into these enums at
        /// prepatch, so Enum.GetNames/GetValues report them for real; anything below
        /// ModdedIdFloor is vanilla and identical for everyone, so it is left out. A type that
        /// won't resolve is skipped, not fatal: a partial answer still beats no handshake.</summary>
        private static List<string> RuntimeEnumEntries()
        {
            var lines = new List<string>();
            foreach (var typeName in ModdedEnumTypeNames)
            {
                try
                {
                    var t = AccessTools.TypeByName(typeName);
                    if (t == null || !t.IsEnum)
                        continue;
                    // GetNames and GetValues are documented to run in the same (binary-value)
                    // order, so index i is one member - walking them in parallel keeps aliases
                    // (two names, one id) as the two distinct lines the registry file shows.
                    var names = Enum.GetNames(t);
                    var values = Enum.GetValues(t);
                    int n = Math.Min(names.Length, values.Length);
                    for (int i = 0; i < n; i++)
                    {
                        long id;
                        try
                        {
                            id = Convert.ToInt64(values.GetValue(i));
                        }
                        catch { continue; }
                        if (id < ModdedIdFloor)
                            continue;
                        lines.Add(t.Name + ":" + names[i] + "=" + id);
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("enum walk (" + typeName + "): " + e.Message); }
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        private static readonly System.Text.RegularExpressions.Regex WhitespaceRx =
            new System.Text.RegularExpressions.Regex("\\s");
        private static readonly System.Text.RegularExpressions.Regex NonWordRx =
            new System.Text.RegularExpressions.Regex("[^A-Za-z0-9_]");

        /// <summary>Mirror of EPL's own string.Sanitize() (EnhancedPrefabLoaderPrepatch: strip all
        /// whitespace, replace every non-word character with '_', prefix '_' when the result is
        /// empty or starts with a digit; blank input becomes "_Invalid"). EPL applies it to a
        /// registry key BEFORE minting the enum member, but writes the RAW key back into
        /// enum_values.json - so the two oracles this class reads disagree by construction: the
        /// runtime walk sees the member "GoldenBooster" while the file says "Golden Booster".
        /// Every name-keyed comparison downstream (the ID-conflict gate, the "already synced"
        /// check, EnumMap's name-&gt;id tables) then treats them as different names, which makes a
        /// GENUINE id conflict on such a name invisible. Normalizing the FILE side here is what
        /// puts the two back in the same key space; RuntimeEnumEntries needs nothing, because
        /// Enum.GetNames already returns the sanitized member names.</summary>
        private static string SanitizeMemberName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0)
                return "_Invalid";
            string s = WhitespaceRx.Replace(value, "");
            s = NonWordRx.Replace(s, "_");
            if (s.Length > 0 && !char.IsDigit(s[0]))
                return s;
            return "_" + s;
        }

        /// <summary>Parse EPL's two-level registry ({ "EnumType": { "Name": id, ... }, ... })
        /// into sorted "EnumType:Name=id" lines, with each NAME put through SanitizeMemberName
        /// so these lines key identically to the runtime walk's. The file is machine-written with
        /// exactly this shape (integer leaves, no nested objects inside a section), so a compact
        /// regex walk is safe; returns null on anything surprising so the caller can fall back.</summary>
        private static List<string> CanonicalEnumLines(string json)
        {
            try
            {
                var outLines = new List<string>();
                // sections: "Name": { ...no nested braces (leaves are ints)... }
                var secRx = new System.Text.RegularExpressions.Regex(
                    "\"([^\"]+)\"\\s*:\\s*\\{([^{}]*)\\}",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                var pairRx = new System.Text.RegularExpressions.Regex(
                    "\"([^\"]+)\"\\s*:\\s*(-?\\d+)");
                foreach (System.Text.RegularExpressions.Match sec in secRx.Matches(json))
                {
                    string section = sec.Groups[1].Value;
                    foreach (System.Text.RegularExpressions.Match pr in pairRx.Matches(sec.Groups[2].Value))
                        outLines.Add(section + ":" + SanitizeMemberName(pr.Groups[1].Value) + "=" + pr.Groups[2].Value);
                }
                if (outLines.Count == 0)
                    return null; // nothing recognizable: let the byte hash decide
                outLines.Sort(StringComparer.Ordinal);
                return outLines;
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        /// <summary>The .coopbak-* file InstallEnumFile set aside IN THIS PROCESS, i.e. the one
        /// holding the exact registry bytes this process booted with. Null until an install
        /// happens here (a backup from a previous run is somebody else's history and tells us
        /// nothing about our runtime). RestoreEnumBackup compares against it: putting THIS file
        /// back is the only restore that provably returns the disk to what we are running.</summary>
        private static string _installBackupPath;

        /// <summary>Install the host's registry over ours, keeping timestamped backups
        /// (the newest 3). Returns a user-facing status line, and raises RestartRequiredForJoin
        /// when bytes actually landed - the "already synced" early-out does NOT raise it, because
        /// nothing was written and this process is still in step with its own file.</summary>
        public static string InstallEnumFile(byte[] hostBytes)
        {
            string p = EnumFilePath();
            try
            {
                string bak = null;
                if (File.Exists(p))
                {
                    var current = File.ReadAllBytes(p);
                    // "already synced" must be a CANONICAL comparison, not bytes: EPL
                    // rewrites the file at boot, so a byte compare told the user "already
                    // synced - RESTART" on every attempt while the (equally byte-bound)
                    // host hash kept rejecting - the two halves of the endless loop. With
                    // canonical hashing on both sides this branch should rarely fire at
                    // all; when it does, the registries genuinely agree and the user's
                    // next join will pass.
                    var curLines = CanonicalEnumLines(System.Text.Encoding.UTF8.GetString(current));
                    var newLines = CanonicalEnumLines(System.Text.Encoding.UTF8.GetString(hostBytes));
                    bool same = curLines != null && newLines != null
                        ? string.Join("\n", curLines) == string.Join("\n", newLines)
                        : SameBytes(current, hostBytes);
                    if (same)
                        return "card database already synced - RESTART the game, then join again";
                    bak = p + ".coopbak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(p, bak, overwrite: true);
                    // Remember WHICH backup holds the bytes this process booted with - the FIRST
                    // install's backup, and only that one. A second install in the same process
                    // sets aside the FIRST HOST's file, which is not what we are running, so
                    // restoring it would owe a restart like any other foreign registry.
                    if (_installBackupPath == null && !RestartRequiredForJoin)
                        _installBackupPath = bak;
                    PruneBackups(p);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                }
                File.WriteAllBytes(p, hostBytes);
                // Deliberately NO cache invalidation here (see EnumFilePath): our identity is
                // what this process loaded at prepatch, and writing the file changed none of it.
                // The 1.0.33 "_enum = null" re-read these fresh bytes and handed the guest a
                // hash matching the host while the running game still held the old ids - a pass
                // through the very gate the restart requirement exists to close. The flag below
                // is the honest version of that signal.
                RestartRequiredForJoin = true;
                // Drop a marker so the mod KNOWS the machine-global registry is now the HOST's,
                // not the guest's own. Without a restore path a mismatched-enum join used to
                // silently brick every modded SOLO save ("data lost") until the file was fixed
                // by hand; HostEnumInstalled() reads this marker to offer a one-click restore,
                // RestoreEnumBackup() clears it. We record the backup just made so the human (and
                // the restore) can find the guest's own file.
                WriteEnumMarker(bak);
                return "card database synced from host (your old file was backed up) - RESTART the game, then join again";
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("enum sync failed: " + e.Message);
                return "could not update the card database automatically - copy the host's enum_values.json manually (see mod page)";
            }
        }

        /// <summary>Marker written beside enum_values.json while the host's registry is on loan
        /// in place of the guest's own. Its mere existence is the signal that a restore is owed.</summary>
        private static string EnumMarkerPath()
        {
            return EnumFilePath() + ".hostlend";
        }

        private static void WriteEnumMarker(string newestBackup)
        {
            try
            {
                string content =
                    (newestBackup != null ? Path.GetFileName(newestBackup) : "(no prior registry - none to back up)")
                    + Environment.NewLine + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(EnumMarkerPath(), content);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("enum marker write failed: " + e.Message); }
        }

        /// <summary>True while the host's registry is installed over the guest's own (the marker
        /// exists) AND this process actually loads a registry. Cheap File.Exists on purpose -
        /// there's no caching to go stale after a restore. CoopCore reads this to offer the
        /// restore action, and a HOST reads it to refuse a conflicting guest.
        ///
        /// 1.0.36: the !EplLoaded() short-circuit. The marker sits beside enum_values.json in
        /// LocalLow and outlives an EPL uninstall exactly like the registry does, so on a game
        /// that loads no registry it is pure litter - and a "true" here is expensive litter. It
        /// pins a permanent "your card database is borrowed" banner in the co-op window, and on
        /// a HOST it fires a hard reject in the Hello handler that ALSO skips sending the guest
        /// the very registry that would have fixed them. A borrowed registry is meaningless to a
        /// process that borrows nothing: answer no.</summary>
        public static bool HostEnumInstalled()
        {
            try
            {
                if (!EplLoaded())
                    return false;
                return File.Exists(EnumMarkerPath());
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

        /// <summary>Undo a host-enum lend: put the guest's OWN registry back so their modded solo
        /// saves load again. Finds the newest .coopbak-* (the file we set aside at install time),
        /// first copies the CURRENT (host's) file to .hostcopy so nothing is ever destroyed, then
        /// restores the backup over enum_values.json and clears the marker. The game reads the
        /// registry once at startup, so <paramref name="message"/> tells the user to restart
        /// before loading solo saves, and a successful restore raises RestartRequiredForSolo.
        /// That flag says exactly one thing: the restored file has not been LOADED yet. It says
        /// nothing about whose ids we are running - a restore, like an install, cannot change
        /// those; this process keeps whatever it booted with either way. Which is why a restore
        /// must never gate a JOIN (it used to, and a housekeeping click at the title screen then
        /// cost a second full restart before the player could accept an invite).
        /// When there is NO backup to restore, nothing is put back and no restart is owed, but
        /// the marker is CLEARED and this still returns true - see that branch for why leaving it
        /// was an unclearable latch. Returns false only when the disk refused the work. CoopCore
        /// and CoopUI call this by name.</summary>
        public static bool RestoreEnumBackup(out string message)
        {
            string p = EnumFilePath();
            try
            {
                var dir = Path.GetDirectoryName(p);
                string newest = null;
                if (Directory.Exists(dir))
                {
                    var baks = Directory.GetFiles(dir, Path.GetFileName(p) + ".coopbak-*");
                    Array.Sort(baks, StringComparer.Ordinal); // timestamp suffix sorts oldest-first
                    if (baks.Length > 0)
                        newest = baks[baks.Length - 1];
                }
                if (newest == null)
                {
                    // Nothing to hand back: either there was no registry of the player's own when
                    // the lend happened (InstallEnumFile records "(no prior registry...)" in the
                    // marker for exactly this case), or the .coopbak-* files have since been
                    // pruned or cleaned out by hand.
                    //
                    // 1.0.36 - THE UNCLEARABLE LATCH. This used to leave the marker in place "so
                    // the prompt can reappear". It reappeared forever: the only thing that clears
                    // the marker is a successful restore, and a successful restore is precisely
                    // what cannot happen when there is no backup. The player was left with a
                    // permanent warning banner and a button that only ever printed the same
                    // refusal - and far worse, if they ever HOSTED, HostEnumInstalled() stayed
                    // true and hard-rejected every conflicting guest without sending them the
                    // registry, so the marker permanently blocked co-op on that PC.
                    //
                    // The honest end state when nothing was ever set aside is "nothing is on
                    // loan": clear the marker. Nothing was WRITTEN to enum_values.json, so
                    // RestartRequiredForSolo deliberately stays false - no file changed under the
                    // running game, so no restart is owed - and the message says plainly that the
                    // registry itself was left alone, with the manual path for a player whose
                    // solo saves really are broken.
                    try
                    {
                        File.Delete(EnumMarkerPath());
                    }
                    catch (Exception e) { CoopPlugin.Log.LogWarning("enum marker clear: " + e.Message); }
                    // Judge success by the END STATE, not by whether Delete threw: a missing
                    // PrefabLoader directory throws DirectoryNotFoundException even though the
                    // marker is (trivially) gone, and telling the player to go delete a file
                    // that isn't there is exactly the kind of dead-end this fix exists to remove.
                    bool cleared;
                    try
                    {
                        cleared = !File.Exists(EnumMarkerPath());
                    }
                    catch { cleared = false; }
                    message = cleared
                        ? "no backup of your card database was found, so your card database was left exactly as it is - the 'borrowed from a host' flag has been cleared (it was blocking hosting). If your solo saves still won't load, put your own enum_values.json back by hand (see mod page)."
                        : "no backup of your card database was found, and the co-op marker could not be deleted - delete enum_values.json.hostlend by hand (it sits next to enum_values.json; see mod page)";
                    // ALSO LOG IT. Clearing the marker makes HostEnumInstalled() false, so the
                    // UI banner this message hangs under disappears on the very next OnGUI pass -
                    // the player can end up seeing no on-screen outcome at all. The log is the
                    // one place the outcome is guaranteed to survive.
                    try
                    {
                        CoopPlugin.Log.LogWarning("enum restore: " + message);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    return cleared;
                }
                // Preserve the host's installed file first so a restore is never a one-way loss.
                if (File.Exists(p))
                    File.Copy(p, p + ".hostcopy", overwrite: true);
                File.Copy(newest, p, overwrite: true);
                // TWO WRITES THAT CANCEL. If this is the very backup THIS process made when it
                // installed the host's file, the disk now provably holds the bytes we booted with:
                // nothing changed under the running game, so nothing is owed. Clearing
                // RestartRequiredForJoin here is the point - leaving it set kept a player who
                // installed and then immediately undid it locked out of joining (and, via
                // RegistryFileMatchesRuntime, out of auto-syncing anyone as host) until they
                // restarted for no reason at all.
                if (_installBackupPath != null
                    && string.Equals(newest, _installBackupPath, StringComparison.OrdinalIgnoreCase))
                {
                    RestartRequiredForJoin = false;
                    RestartRequiredForSolo = false;
                    _installBackupPath = null; // that backup's bytes are on disk now, not aside
                    try
                    {
                        File.Delete(EnumMarkerPath());
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    message = "your card database was restored from the backup this session made - it is exactly what the game is already running, so NO restart is needed";
                    try
                    {
                        CoopPlugin.Log.LogInfo("enum restore: " + message + " (from " + Path.GetFileName(newest) + ")");
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    return true;
                }
                // Any OTHER backup is a registry this process never loaded: same invariant as
                // InstallEnumFile (see EnumFilePath) - the file on disk is the guest's own again,
                // but the running process still holds the ids it booted with, so nothing about
                // our identity moves until a restart reloads them. SOLO-save concern only.
                RestartRequiredForSolo = true;
                try
                {
                    File.Delete(EnumMarkerPath());
                }
                catch (System.Exception e) { Swallow.Log(e); }
                message = "your card database was restored from backup - RESTART the game before loading your solo saves";
                // Same reason as the no-backup branch: the marker is gone, so the banner the UI
                // shows this under is gone too. Name the backup we used - a player who restored
                // the wrong one needs to know which file went back.
                try
                {
                    CoopPlugin.Log.LogInfo("enum restore: " + message + " (from " + Path.GetFileName(newest) + ")");
                }
                catch (System.Exception e) { Swallow.Log(e); }
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("enum restore failed: " + e.Message);
                message = "could not restore your card database automatically - put your own enum_values.json backup back by hand (see mod page)";
                return false;
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static void PruneBackups(string basePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(basePath);
                var baks = Directory.GetFiles(dir, Path.GetFileName(basePath) + ".coopbak-*");
                Array.Sort(baks, StringComparer.Ordinal); // timestamp suffix sorts oldest-first
                for (int i = 0; i < baks.Length - 3; i++)
                    File.Delete(baks[i]);
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        private static string Sha1(string s)
        {
            return Sha1Bytes(Encoding.UTF8.GetBytes(s));
        }

        private static string Sha1Bytes(byte[] data)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }

        private static string Short(string hex)
        {
            return hex.Length > 16 ? hex.Substring(0, 16) : hex;
        }
    }
}
