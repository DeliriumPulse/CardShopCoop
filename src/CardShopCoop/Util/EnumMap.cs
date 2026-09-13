using System;
using System.Collections.Generic;

namespace CardShopCoop.Util
{
    /// <summary>The id spaces that can legitimately DIFFER between two PCs running the same
    /// content packs, and therefore the only ones that need translating at the wire boundary.
    /// Four of them are enums EnhancedPrefabLoader mints into (see ModParity's
    /// ModdedEnumTypeNames); MonsterType is not EPL's at all - custom monsters come from
    /// CreateCards/CardForge .ini files - so it is built from a different source and obeys a
    /// different "is this id modded?" rule. See EnumMap.Build.</summary>
    public enum EnumKind
    {
        ItemType = 0,      // EItemType
        ObjectType = 1,    // EObjectType
        DecoObject = 2,    // EDecoObject
        CardExpansion = 3, // ECardExpansionType
        MonsterType = 4,   // EMonsterType (CreateCards/CardForge, NOT EPL)
    }

    /// <summary>
    /// NAME-BASED id translation at the wire boundary, so two PCs whose modded ids are a
    /// PERMUTATION of each other (the everyday case: the same content packs installed in a
    /// different filesystem order, which EPL mints ids for in unsorted discovery order) can
    /// play together without either player's registry being overwritten.
    ///
    /// THE THREE RULES THIS CLASS EXISTS TO ENFORCE - break any of them and the regression
    /// surface stops being "modded ids" and becomes "everything":
    ///
    ///  1. THE WIRE ALWAYS SPEAKS HOST IDS. The host's id space is canonical. Only a CLIENT
    ///     translates (outgoing local-&gt;host, incoming host-&gt;local); the HOST translates
    ///     nothing and never builds a table. Build() is called from exactly one place - the
    ///     client's Welcome handler - which is what keeps that true. A process that is hosting
    ///     has no tables, so every call here is the identity function.
    ///
    ///  2. TRANSLATION IS A NO-OP ON VANILLA IDS. Vanilla enum members are dense 0..~135 (plus
    ///     a None sentinel at -1 or 0) and are IDENTICAL on every PC, so anything below
    ///     ModdedIdFloor is returned unchanged without so much as a dictionary probe. Vanilla
    ///     traffic is therefore provably unaffected by anything in this file. MonsterType is
    ///     the documented exception - CardForge ids live INSIDE the vanilla dense band - so it
    ///     carries an explicit set of "these ids are the custom ones" instead of a floor.
    ///
    ///  3. THE SAFE DEFAULT IS ALWAYS "UNCHANGED". No tables built (vanilla, not yet connected,
    ///     hosting, a missing or unparseable blob from the host) means identity in both
    ///     directions. Degrade to today's behavior, never to garbage.
    ///
    /// A modded id whose NAME has no counterpart on this PC is NOT an error and must never be
    /// treated as one: one-sided content packs are explicitly allowed. It yields the game's own
    /// None sentinel for that enum, which the existing skip paths refuse (see
    /// CoopCore.CardSetInstalledHere, which had to learn about None for exactly this reason).
    /// Callers that need to BRANCH on it use TryFromWire.
    /// </summary>
    public static class EnumMap
    {
        private const int KindCount = 5;

        /// <summary>First id EPL hands out - the same constant ModParity filters its registry
        /// lines with, repeated here rather than shared because the two must be able to move
        /// independently if EPL ever changes it for one enum and not another. Anything below
        /// this is vanilla: identical on every PC, so translating it is at best a waste and at
        /// worst the bug. Deliberately does NOT apply to MonsterType (see Build).</summary>
        private const int ModdedIdFloor = 200000;

        /// <summary>One direction of one kind. <see cref="ModdedSource"/> is the answer to "is
        /// this SOURCE-side id one of the modded ones?", which is what decides whether an id is
        /// eligible for translation at all: null means "use ModdedIdFloor" (the EPL enums), a
        /// non-null set means "modded is exactly these ids" (MonsterType, whose custom ids sit
        /// in the middle of the vanilla band and cannot be told apart by magnitude).</summary>
        private sealed class Table
        {
            public readonly Dictionary<int, int> Map = new Dictionary<int, int>();
            public HashSet<int> ModdedSource;

            public bool IsModded(int id)
            {
                return ModdedSource == null ? id >= ModdedIdFloor : ModdedSource.Contains(id);
            }
        }

        // Indexed by (int)EnumKind. A null entry means "no table for this kind" -> identity.
        // Published as whole arrays and read without a lock: Build runs once, on the main
        // thread, inside the Welcome handler, before any other message is dispatched, and
        // _active is written LAST so a reader can never see a half-filled table.
        private static Table[] _outTables;
        private static Table[] _inTables;
        private static volatile bool _active;

        /// <summary>One log line per (kind,id) that could not be translated. A card price heal
        /// re-sends every displayed card every ~30s, so without this memo a one-sided content
        /// pack would print the same line thousands of times in an evening.</summary>
        private static readonly HashSet<long> _loggedMisses = new HashSet<long>();

        /// <summary>The last "id translation ready" summary actually logged at Info, so an
        /// identical one is demoted to Debug instead of repeating.
        ///
        /// DO NOT reset this in <see cref="Clear"/>. Clear runs on EVERY teardown and on BOTH
        /// host-start paths, i.e. at least once between any two Builds - clearing it would make
        /// every summary look new again, restore the per-join spam, and turn this into a no-op.
        /// A stale value across sessions is harmless: it only ever suppresses a duplicate line,
        /// and the moment the content set differs the text differs and it logs.</summary>
        private static string _lastSummary;

        /// <summary>How many times <see cref="Build"/> has run this process - i.e. the join
        /// number, since Build has exactly one call site (the client's Welcome handler). Printed
        /// with the repeat line so a field log still shows that a re-join HAPPENED even when the
        /// summary text is identical. NOT reset in <see cref="Clear"/>, same rule and same reason
        /// as <see cref="_lastSummary"/> above (Clear runs between any two Builds).</summary>
        private static int _buildCount;

        /// <summary>The value handed back for a modded id with no counterpart on this PC. The
        /// game's OWN "nothing" member for that enum, checked against the decompiled sources
        /// rather than assumed - because the convention is NOT uniform: EItemType, EObjectType
        /// and ECardExpansionType all declare None = -1, but EDecoObject.None = 0 and
        /// EMonsterType.None = 0 (EMonsterType's -1 is EarlyPlayer, a real member). Handing -1
        /// to a kind whose None is 0 would produce a value that is not a member at all.
        ///
        /// NOTE for anyone extending this: every one of these sentinels IS a defined enum
        /// member, so an Enum.IsDefined-style guard does NOT reject it on its own. Any skip
        /// path that has to refuse an unmappable id must test for None explicitly.</summary>
        private static int Sentinel(EnumKind kind)
        {
            switch (kind)
            {
                case EnumKind.DecoObject:
                    return 0;  // EDecoObject.None = 0
                case EnumKind.MonsterType:
                    return 0; // EMonsterType.None = 0 (EarlyPlayer = -1)
                default:
                    return -1;                  // EItemType/EObjectType/ECardExpansionType.None = -1
            }
        }

        /// <summary>The enum type name ModParity writes into its registry lines, per kind.
        /// Null for MonsterType, which never appears in those lines.</summary>
        private static readonly string[] EnumTypeNames =
        {
            "EItemType", "EObjectType", "EDecoObject", "ECardExpansionType", null
        };

        // ------------------------------------------------------------------ public API

        /// <summary>CLIENT: our local id -&gt; the host's id. HOST (or before any table is built):
        /// the identity function. Below ModdedIdFloor - i.e. all vanilla traffic - always the
        /// identity function. Returns the kind's None sentinel when the local name has no
        /// counterpart on the host (a content pack only WE have), which the receiving side's
        /// existing skip paths refuse.</summary>
        public static int ToWire(EnumKind kind, int localId)
        {
            return Translate(_outTables, kind, localId, "local->host");
        }

        /// <summary>CLIENT: a host id off the wire -&gt; our local id. HOST (or before any table
        /// is built): the identity function. Same vanilla no-op and same None sentinel as
        /// <see cref="ToWire"/>; use <see cref="TryFromWire"/> when the caller must branch on
        /// "this PC does not have that content".</summary>
        public static int FromWire(EnumKind kind, int wireId)
        {
            return Translate(_inTables, kind, wireId, "host->local");
        }

        /// <summary>Like <see cref="FromWire"/>, but says whether the id actually RESOLVED.
        /// False means the host sent a modded id whose name does not exist on this PC - a
        /// one-sided content pack, which is allowed - and <paramref name="localId"/> is the
        /// None sentinel. True with the translated (or unchanged vanilla) id otherwise.</summary>
        public static bool TryFromWire(EnumKind kind, int wireId, out int localId)
        {
            localId = wireId;
            if (!_active)
                return true;                       // identity: nothing to fail at
            var t = TableFor(_inTables, kind);
            if (t == null || !t.IsModded(wireId))
                return true; // vanilla / untranslated kind
            if (t.Map.TryGetValue(wireId, out localId))
                return true;
            localId = Sentinel(kind);
            LogMissOnce(kind, wireId, "host->local");
            return false;
        }

        /// <summary>Drop every table: the session's canonical id space died with it. Called
        /// from CoopCore.Shutdown (the per-session reset) so the next session - which may be a
        /// session where WE are the host, and the host must translate nothing - starts from the
        /// safe default of identity.</summary>
        public static void Clear()
        {
            _active = false;
            _outTables = null;
            _inTables = null;
            // Same lock LogMissOnce takes. HashSet is not thread-safe and translation runs off
            // whatever thread a message is handled on, so clearing unguarded could corrupt the
            // set (or throw) while a miss is being recorded.
            lock (_loggedMisses)
            {
                _loggedMisses.Clear();
            }
        }

        /// <summary>Alias for <see cref="Clear"/> - both names get reached for.</summary>
        public static void Reset()
        {
            Clear();
        }

        /// <summary>True while a translation table is live (i.e. we are a client that received
        /// the host's registry). Diagnostics only - correctness must never depend on asking.</summary>
        public static bool Active
        {
            get
            {
                return _active;
            }
        }

        // ------------------------------------------------------------------ construction

        /// <summary>CLIENT ONLY: build the translation tables from the host's registry (received
        /// in Welcome) and our own (read locally). Call this before any other message from the
        /// session is processed; call it exactly once per session.
        ///
        /// <paramref name="hostEnumLines"/> are canonical "EnumType:Name=id" lines - the same
        /// shape ModParity.EnumLines() produces here. <paramref name="hostCardLines"/> are
        /// CreateCards/CardForge "MonsterName=id" lines - the shape ModParity.CardsList()
        /// produces here.
        ///
        /// A kind is only tabled when BOTH sides have modded names for it. An empty list -
        /// theirs (blob missing, unparseable, or a vanilla host) or ours - leaves that half
        /// UNBUILT, which means identity, which means today's behavior. That is deliberate:
        /// mapping every id of a populated side against an empty one would turn a session that
        /// works today into a session where nothing modded resolves.
        ///
        /// WHY MONSTERTYPE CANNOT USE THE ID FLOOR: CardForge picks a Monster Type ID inside the
        /// expansion's vanilla band (its own spec: strictly between the expansion's last vanilla
        /// id and its Max, e.g. just above 121 for Tetramon), so custom monsters are numbered in
        /// the low hundreds, nowhere near 200000. A floor test would classify every custom
        /// monster as "vanilla, don't touch" and silently translate nothing. The set of ids in
        /// the .ini-derived card list IS the definition of "modded" for this kind, per side.</summary>
        public static void Build(List<string> hostEnumLines, List<string> hostCardLines)
        {
            Clear();
            _buildCount++; // every Build is a join; Clear() must not reset this (see the field)
            try
            {
                var outTables = new Table[KindCount];
                var inTables = new Table[KindCount];
                var summary = new List<string>();

                // ---- the four EPL enums, keyed by name within each enum type
                var ourEnum = ParseEnumLines(SafeLines(ModParity.EnumLines));
                var theirEnum = ParseEnumLines(hostEnumLines);
                for (int k = 0; k < KindCount; k++)
                {
                    if (EnumTypeNames[k] == null)
                        continue;         // MonsterType, handled below
                    // BOTH sides must have modded names of this kind before a table is worth
                    // building. An empty side has two causes we cannot tell apart - genuinely
                    // vanilla, or our own registry walk came up empty on a modded PC - and only
                    // the first is safe to act on. Skipping means identity, i.e. exactly what
                    // this build did before translation existed.
                    if (ourEnum[k].Count == 0 || theirEnum[k].Count == 0)
                        continue;
                    var pair = BuildPair(ourEnum[k], theirEnum[k], null, null);
                    outTables[k] = pair.Item1;
                    inTables[k] = pair.Item2;
                    summary.Add(Describe((EnumKind)k, ourEnum[k], theirEnum[k], pair.Item1));
                }

                // ---- MonsterType: CreateCards/CardForge, not EPL, and NOT floor-separable
                var ourCards = ParseCardLines(SafeLines(ModParity.CardsList));
                var theirCards = ParseCardLines(hostCardLines);
                if (ourCards.Count > 0 && theirCards.Count > 0)
                {
                    int mk = (int)EnumKind.MonsterType;
                    var pair = BuildPair(ourCards, theirCards, IdSet(ourCards), IdSet(theirCards));
                    outTables[mk] = pair.Item1;
                    inTables[mk] = pair.Item2;
                    summary.Add(Describe(EnumKind.MonsterType, ourCards, theirCards, pair.Item1));
                }

                _outTables = outTables;
                _inTables = inTables;
                _active = true;   // written LAST: readers see complete tables or none at all
                // Compacted, NOT silenced, when it says the same thing as last time. Every
                // emission of this line IS a genuine re-join (Build has one call site, the
                // client's Welcome handler, as SendWorldTo does on the host side), and 14 joins
                // in one evening was a real session - so the line is not spurious, it is just
                // repetitive, and the interesting event is the summary CHANGING (a content pack
                // came or went). This used to go to LogDebug, which BepInEx does not write to
                // the disk log by default, so a re-join left NO trace in the logs people
                // actually send us. Info + the join counter keeps it one short line per join.
                string line = "id translation ready (wire speaks HOST ids): " +
                    (summary.Count > 0 ? string.Join("; ", summary.ToArray()) : "nothing modded on either side - identity");
                if (!string.Equals(line, _lastSummary, StringComparison.Ordinal))
                {
                    _lastSummary = line;
                    Log(line);
                }
                else
                    Log("id translation ready (unchanged, join #" + _buildCount + ")");
            }
            catch (Exception e)
            {
                // A translation table is an optimization over "reject the join"; it must never
                // be the thing that breaks one. Fall all the way back to identity.
                Clear();
                LogWarn("id translation could not be built (" + e.Message + ") - running untranslated, as previous versions did");
            }
        }

        /// <summary>local-&gt;host and host-&gt;local for one kind, from the two name-&gt;id tables.
        /// A name on only ONE side gets no entry in either direction - that is the allowed
        /// one-sided content pack, and it is what makes the lookup miss (and so the None
        /// sentinel) mean something specific.</summary>
        private static Tuple<Table, Table> BuildPair(Dictionary<string, int> ours, Dictionary<string, int> theirs,
            HashSet<int> ourModded, HashSet<int> theirModded)
        {
            var toHost = new Table { ModdedSource = ourModded };
            var toLocal = new Table { ModdedSource = theirModded };
            foreach (var kv in ours)
            {
                int hostId;
                if (!theirs.TryGetValue(kv.Key, out hostId))
                    continue;
                // Indexer, not Add: EPL registries can carry ALIASES (two names, one id), and a
                // duplicate key must not throw a handshake to the floor. Last one wins, which is
                // the same rule CoopCore's own registry parse already uses.
                toHost.Map[kv.Value] = hostId;
                toLocal.Map[hostId] = kv.Value;
            }
            return Tuple.Create(toHost, toLocal);
        }

        private static string Describe(EnumKind kind, Dictionary<string, int> ours,
            Dictionary<string, int> theirs, Table toHost)
        {
            int mapped = toHost.Map.Count;
            return kind + " " + mapped + " mapped/" + Math.Max(0, ours.Count - mapped) +
                   " ours-only/" + Math.Max(0, theirs.Count - mapped) + " host-only";
        }

        // ------------------------------------------------------------------ internals

        private static int Translate(Table[] tables, EnumKind kind, int id, string dir)
        {
            if (!_active)
                return id;                        // host, or not connected: identity
            var t = TableFor(tables, kind);
            if (t == null || !t.IsModded(id))
                return id;    // vanilla id, or no table for this kind
            int mapped;
            if (t.Map.TryGetValue(id, out mapped))
                return mapped;
            LogMissOnce(kind, id, dir);
            return Sentinel(kind);
        }

        private static Table TableFor(Table[] tables, EnumKind kind)
        {
            var t = tables;
            if (t == null)
                return null;
            int i = (int)kind;
            return i >= 0 && i < t.Length ? t[i] : null;
        }

        private static void LogMissOnce(EnumKind kind, int id, string dir)
        {
            long key = ((long)(int)kind << 32) | (uint)id;
            bool first;
            lock (_loggedMisses)
            {
                first = _loggedMisses.Add(key);
            }
            if (first)
                Log("no local counterpart for " + kind + " id " + id + " (" + dir +
                    ") - one-sided content pack; sent as None, further ones for this id are silent");
        }

        /// <summary>"EnumType:Name=id" lines -&gt; one name-&gt;id dictionary per kind. Only the
        /// four EPL enum kinds are populated; ids below the floor are dropped because vanilla
        /// members are identical everywhere and an entry for one could only ever be a no-op.</summary>
        private static Dictionary<string, int>[] ParseEnumLines(List<string> lines)
        {
            var byKind = new Dictionary<string, int>[KindCount];
            for (int i = 0; i < KindCount; i++)
                byKind[i] = new Dictionary<string, int>(StringComparer.Ordinal);
            if (lines == null)
                return byKind;
            foreach (var raw in lines)
            {
                if (string.IsNullOrEmpty(raw))
                    continue;
                string line = raw.Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                int kind = KindOfTypeName(line.Substring(0, colon));
                if (kind < 0)
                    continue;                       // ERarity / ECollectionPackType: nothing crosses the wire
                int eq = line.LastIndexOf('=');               // LAST '=': a member name may contain one
                if (eq <= colon + 1 || eq == line.Length - 1)
                    continue;
                int id;
                if (!int.TryParse(line.Substring(eq + 1), out id))
                    continue;
                if (id < ModdedIdFloor)
                    continue;
                byKind[kind][line.Substring(colon + 1, eq - colon - 1)] = id;
            }
            return byKind;
        }

        /// <summary>"MonsterName=id" lines (ModParity.CardsList) -&gt; name-&gt;id. No floor filter:
        /// see Build for why CardForge ids cannot be told from vanilla ones by magnitude.</summary>
        private static Dictionary<string, int> ParseCardLines(List<string> lines)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (lines == null)
                return map;
            foreach (var raw in lines)
            {
                if (string.IsNullOrEmpty(raw))
                    continue;
                string line = raw.Trim();
                int eq = line.LastIndexOf('=');
                if (eq <= 0 || eq == line.Length - 1)
                    continue;
                int id;
                if (!int.TryParse(line.Substring(eq + 1).Trim(), out id))
                    continue;
                map[line.Substring(0, eq).Trim()] = id;
            }
            return map;
        }

        private static HashSet<int> IdSet(Dictionary<string, int> nameToId)
        {
            var s = new HashSet<int>();
            foreach (var kv in nameToId)
                s.Add(kv.Value);
            return s;
        }

        private static int KindOfTypeName(string typeName)
        {
            for (int i = 0; i < KindCount; i++)
                if (EnumTypeNames[i] != null && string.Equals(EnumTypeNames[i], typeName, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        /// <summary>Our own registry reads must never throw into a session handshake.</summary>
        private static List<string> SafeLines(Func<List<string>> f)
        {
            try
            {
                return f() ?? new List<string>();
            }
            catch (System.Exception e) { Swallow.Log(e); return new List<string>(); }
        }

        private static void Log(string s)
        {
            try
            {
                CoopPlugin.Log.LogInfo("EnumMap: " + s);
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        private static void LogWarn(string s)
        {
            try
            {
                CoopPlugin.Log.LogWarning("EnumMap: " + s);
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }
    }
}
