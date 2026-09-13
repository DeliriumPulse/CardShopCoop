using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        /// <summary>Has the "your card database is the host's" warning already been logged this
        /// process? One-shot memo shared by the Awake fast path and EnumLendState.</summary>
        private static bool _enumLendWarned;

        /// <summary>FIX A-hook: exposed for the co-op UI (CoopUI, owned elsewhere). Returns a
        /// one-line notice when the on-disk enum registry is a host-synced copy - so the UI
        /// can show it and offer a restore button - or null when the registry is the user's
        /// own. The restore itself is Util.ModParity.RestoreEnumBackup(out msg).
        ///
        /// THE LOG WARNING LIVES HERE, NOT IN AWAKE. HostEnumInstalled() short-circuits on
        /// !EplLoaded(), and EplLoaded() probes for EPL's PLUGIN assembly types - which BepInEx
        /// may not have chainloaded yet when our Awake runs. A single call at Awake therefore
        /// answers "no" on exactly the modded machines the warning was written for, and being a
        /// one-shot, the warning was then lost for the whole session. CoopUI polls this every
        /// OnGUI frame, so emitting on the FIRST non-null answer catches it whenever EPL turns
        /// up; the memo keeps it to one line no matter how many frames ask.</summary>
        public static string EnumLendState()
        {
            try
            {
                if (!Util.ModParity.HostEnumInstalled())
                    return null;
                if (!_enumLendWarned)
                {
                    _enumLendWarned = true;
                    CoopPlugin.Log.LogWarning(CoopPlugin.Name + ": your custom-card database is currently the HOST's synced copy from a co-op session. Your OWN solo modded saves may not load until you restore it (restore via the co-op window) and RESTART the game.");
                }
                return "custom-card database is the HOST's copy (co-op sync) - solo modded saves may not load; restore via the co-op window";
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        /// <summary>FIX C: our own runtime registry lines, never throwing into the handshake.
        /// An empty list means "nothing modded here", which can never conflict.</summary>
        private static List<string> SafeEnumLines()
        {
            try
            {
                return Util.ModParity.EnumLines() ?? new List<string>();
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("enum lines: " + e.Message);
                return new List<string>();
            }
        }

        /// <summary>Our CreateCards/CardForge custom-card identity lines ("MonsterName=id"),
        /// never throwing into the handshake. Empty means "no custom cards here".</summary>
        private static List<string> SafeCardsList()
        {
            try
            {
                return Util.ModParity.CardsList() ?? new List<string>();
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("cards list: " + e.Message);
                return new List<string>();
            }
        }

        /// <summary>The one encoding for a registry blob on the wire, factored out of SendHello
        /// so the host's Welcome blobs are byte-identical in shape to the guest's Hello blob:
        /// gzipped "\n"-joined lines, written as [int gzLen][gz bytes] and read back by
        /// ReadCappedEnumBlob under EnumBlobCap. A read failure yields a gzipped EMPTY blob
        /// rather than aborting: the receiver reads that as "nothing modded", which is the
        /// pre-translation behavior and conflicts with nobody.</summary>
        private static byte[] GzipLines(List<string> lines)
        {
            try
            {
                var arr = (lines ?? new List<string>()).ToArray();
                var raw = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", arr));
                // SAY SO WHEN WE ARE ABOUT TO SEND SOMETHING THE READER WILL THROW AWAY. The
                // reader caps the DECOMPRESSED text at EnumBlobCap, and until now the writer
                // never looked: an over-cap registry simply failed inside GunzipCapped on the
                // far side and was logged there as "no registry - fine if the host is vanilla",
                // which is the single most misleading thing we could say about the most heavily
                // modded host on the network. Still SEND it - the reader degrades to identity,
                // which is what this build did before translation existed - but leave a line in
                // the sender's own log that names the size, because that is the only machine
                // where the fix (fewer content packs, or a bigger cap) can be applied.
                if (raw.Length > EnumBlobCap)
                    CoopPlugin.Log.LogWarning("registry blob is OVER THE WIRE CAP: " + arr.Length + " ids, "
                        + raw.Length + " bytes uncompressed vs a " + EnumBlobCap
                        + "-byte cap - the other PC will IGNORE it and modded ids will not be translated this session (ids must already match)");
                return Msg.Gzip(raw);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("registry blob: " + e.Message);
                return Msg.Gzip(new byte[0]);
            }
        }

        /// <summary>Same as ReadCappedEnumBlob but over a blob the DTO already decoded from
        /// the payload. The DTO carries the raw compressed bytes; here we only split them
        /// into lines and fingerprint the digest.</summary>
        private static List<string> ReadCappedEnumBlob(byte[] gz, out string digest)
        {
            digest = "none";
            var lines = new List<string>();
            try
            {
                if (gz == null || gz.Length == 0)
                    return lines;
                string text = GunzipCapped(gz, EnumBlobCap);
                if (text == null)
                    return lines;
                digest = Fnv(text).ToString("X8");
                foreach (var line in text.Split('\n'))
                {
                    string s = line.Trim();
                    if (s.Length > 0)
                        lines.Add(s);
                }
            }
            catch (System.Exception e) { Swallow.Log(e); }
            return lines;
        }

        /// <summary>FIX C wire cap. The real registry is ~100KB of text / ~7KB gzipped, so a
        /// quarter-megabyte is generous for anything honest and small enough that a malformed
        /// (or hostile) Hello - which arrives BEFORE the peer is accepted - can't make the
        /// host allocate its way into trouble.</summary>
        private const int EnumBlobCap = 256 * 1024;

        /// <summary>Bounded gunzip. Msg.Gunzip grows without limit, which is fine for our own
        /// world transfers (we asked for them) but not for a blob an unaccepted peer hands us.
        /// Returns null when the payload isn't valid gzip or blows past the cap - and LOGS which
        /// of the two it was, because the caller's silent empty-list return is otherwise
        /// indistinguishable from "the peer is vanilla and sent nothing", the exact confusion
        /// that had an over-cap modded host reported as a vanilla one.</summary>
        private static string GunzipCapped(byte[] data, int cap)
        {
            try
            {
                using (var src = new MemoryStream(data, writable: false))
                using (var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress))
                using (var dst = new MemoryStream())
                {
                    var buf = new byte[8192];
                    int n;
                    while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (dst.Length + n > cap)
                        {
                            // junk or a decompression bomb - but on an honest peer this is simply
                            // a registry bigger than the cap, so name the cap, not the peer.
                            CoopPlugin.Log.LogWarning("registry blob unpacked OVER CAP (more than "
                                + cap + " bytes from " + data.Length + " compressed) - ignored, NOT a vanilla peer");
                            return null;
                        }
                        dst.Write(buf, 0, n);
                    }
                    return System.Text.Encoding.UTF8.GetString(dst.ToArray());
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("registry blob could not be unpacked (" + data.Length
                    + " bytes, not valid gzip: " + e.Message + ") - ignored");
                return null;
            }
        }

        /// <summary>FIX C: the ONLY registry difference that can corrupt a shared world - the
        /// same "Type:Name" bound to DIFFERENT ids on the two machines. Entries only one side
        /// has are NOT a conflict: nobody can spawn what the other doesn't know about, and the
        /// existing catalog-differs warning already tells both players their sets differ. A
        /// side with no modded entries at all conflicts with nobody, which preserves the old
        /// "none" hash guard. Each result reads "Type:Name -&gt; yours &lt;id&gt;, host &lt;id&gt;".</summary>
        private static List<string> EnumConflicts(List<string> theirs, List<string> ours)
        {
            var found = new List<string>();
            if (theirs == null || theirs.Count == 0 || ours == null || ours.Count == 0)
                return found;
            var theirMap = EnumMap(theirs);
            var ourMap = EnumMap(ours);
            foreach (var kv in theirMap)
            {
                if (ourMap.TryGetValue(kv.Key, out string ourId) && ourId != kv.Value)
                    found.Add($"{kv.Key} -> yours {kv.Value}, host {ourId}");
            }
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>"Type:Name=id" -&gt; { "Type:Name": "id" }. Split on the LAST '=' so a name
        /// containing one still keys correctly; lines without a usable '=' are ignored.</summary>
        private static Dictionary<string, string> EnumMap(List<string> lines)
        {
            var m = new Dictionary<string, string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                    continue;
                int eq = line.LastIndexOf('=');
                if (eq <= 0 || eq == line.Length - 1)
                    continue;
                m[line.Substring(0, eq)] = line.Substring(eq + 1); // last wins on a dup key
            }
            return m;
        }

        /// <summary>Name up to five conflicting entries in a reject line: a bare count leaves
        /// the player with nothing to search their content packs for.</summary>
        private static string DescribeConflicts(List<string> conflicts)
        {
            const int Max = 5;
            int n = Math.Min(conflicts.Count, Max);
            // "; " between entries: each entry already contains a comma ("yours X, host Y")
            string s = string.Join("; ", conflicts.GetRange(0, n).ToArray());
            if (conflicts.Count > n)
                s += $" (+{conflicts.Count - n} more)";
            if (s.Length > 400)
                s = s.Substring(0, 397) + "...";
            return s;
        }

        /// <summary>FIX C: identity for the enum-sync memory below. The Steam id would be
        /// ideal, but the transport keeps its connId-&gt;CSteamID map private - and connId is no
        /// good at all here: a rejected guest is KICKED, restarts the game and comes back on a
        /// fresh connId, which is precisely the round trip the loop-breaker has to recognise.
        /// The player name usually survives it, but it is a free-text config field - blank
        /// (PlayerName cleared) or shared-default names are both real - so the REGISTRY DIGEST
        /// is folded into the key instead of stored as its value. A blank name then falls back
        /// to the digest alone, which still survives the restart, so the loop-breaker fires for
        /// an unnamed player too; and two different registries can never collide onto one key,
        /// so a name collision cannot suppress somebody's first-ever sync. The residual case -
        /// two blank-named peers running the SAME registry - shares a key on purpose: the same
        /// file that couldn't help the first cannot help the second either.</summary>
        private static string PeerSyncKey(string name, string enumDigest)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            string d = string.IsNullOrEmpty(enumDigest) ? "none" : enumDigest;
            return (n.Length > 0 ? "n:" + n : "anon") + "|" + d;
        }

        /// <summary>Loop-breaker memory (host only): how many times we have handed our enum file
        /// to a given (peer identity | registry digest) pair. Counted rather than a bare set
        /// since 1.0.36, because the premise changed: syncing genuinely CAN converge (EPL seeds
        /// its ids from the file it finds - see ModParity.EnumFilePath), so a peer who comes back
        /// with the SAME digest has almost certainly not APPLIED the file yet - they never fully
        /// restarted, the write failed, or the host was itself gated - rather than proved the
        /// file useless. One more attempt (EnumSyncMaxSends) is worth far more than the old flat
        /// refusal, and the counter still keeps the original promise: we never tell a player
        /// "restart and it will work" over and over. A peer who genuinely changed their content
        /// packs hashes to a new key and starts fresh. Incremented only after the file actually
        /// went out. Cleared in Shutdown.</summary>
        private readonly Dictionary<string, int> _enumSyncSentTo = new Dictionary<string, int>();

        /// <summary>The SECOND loop-breaker, keyed on the peer NAME alone, and the reason both
        /// keys exist. The digest-bearing key above is the precise one - a peer who genuinely
        /// changed their content packs SHOULD get a fresh budget - but it is only a terminator
        /// while the digest holds still. EPL re-mints ids whenever it hits a collision, so a
        /// guest in that state hashes to a DIFFERENT registry on every single boot, mints a
        /// brand-new key, and gets the full EnumSyncMaxSends budget again: an unbounded
        /// "synced - RESTART - rejoin" loop wearing a bounded counter's clothes. This counter
        /// cannot be shifted by anything on the guest's disk, so it always terminates. Both are
        /// kept because either alone is wrong: name-only would deny a legitimately re-packed
        /// guest their second chance, digest-only never ends.</summary>
        private readonly Dictionary<string, int> _enumSyncSentToPeer = new Dictionary<string, int>();

        /// <summary>How many times the same peer+registry may be sent our enum file before the
        /// terminal message. Two: one to install, and one for the very common "they clicked
        /// straight back to the title screen instead of quitting to desktop".</summary>
        private const int EnumSyncMaxSends = 2;

        /// <summary>Hard ceiling on sends to ONE peer per hosting session, whatever their registry
        /// digest does. Five, so a guest who really is re-minting ids still gets a couple of
        /// honest retries past the per-digest budget before we call it.</summary>
        private const int EnumSyncMaxSendsPerPeer = 5;

        /// <summary>FIX E3: build a precise reject reason by diffing the joiner's list
        /// against the host's. Entries are "key=value" ("guid=version" for plugins,
        /// "MonsterType=ID" for cards). Reports what the joiner is MISSING (host has, they
        /// don't), what they have EXTRA (they have, host doesn't), and same-key value
        /// mismatches. Returns null when the lists are absent or actually agree (hash
        /// differed on something unlisted) so the caller uses its generic wording. Compact,
        /// capped so a big mod set can't produce a wall of text.</summary>
        private static string DescribeModDiff(List<string> theirs, List<string> ours,
            string head, string diffLabel)
        {
            if (theirs == null || theirs.Count == 0 || ours == null || ours.Count == 0)
                return null;

            var theirMap = DiffMap(theirs);
            var ourMap = DiffMap(ours);

            var missing = new List<string>(); // host has, joiner lacks
            var extra = new List<string>();   // joiner has, host lacks
            var valDiff = new List<string>();  // same key, different value
            foreach (var kv in ourMap)
                if (!theirMap.ContainsKey(kv.Key))
                    missing.Add(kv.Key);
            foreach (var kv in theirMap)
            {
                if (!ourMap.TryGetValue(kv.Key, out var ourVal))
                    extra.Add(kv.Key);
                else if (ourVal != kv.Value)
                    valDiff.Add($"{kv.Key} (host {ourVal} vs yours {kv.Value})");
            }
            if (missing.Count == 0 && extra.Count == 0 && valDiff.Count == 0)
                return null; // lists agree - the hash differed elsewhere; use generic wording

            missing.Sort(StringComparer.Ordinal);
            extra.Sort(StringComparer.Ordinal);
            valDiff.Sort(StringComparer.Ordinal);

            var parts = new List<string>();
            if (missing.Count > 0)
                parts.Add("you are missing: " + JoinCapped(missing));
            if (extra.Count > 0)
                parts.Add("you have extra: " + JoinCapped(extra));
            if (valDiff.Count > 0)
                parts.Add(diffLabel + ": " + JoinCapped(valDiff));
            string full = head + string.Join(" | ", parts);
            // hard cap so a pathological diff can't overflow the reject line / UI
            if (full.Length > 700)
                full = full.Substring(0, 697) + "...";
            return full;
        }

        /// <summary>Split "key=value" entries on the FIRST '=' (values may contain '=');
        /// entries without '=' key on the whole string.</summary>
        private static Dictionary<string, string> DiffMap(List<string> entries)
        {
            var m = new Dictionary<string, string>();
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e))
                    continue;
                int eq = e.IndexOf('=');
                string k = eq > 0 ? e.Substring(0, eq) : e;
                string v = eq > 0 ? e.Substring(eq + 1) : "";
                m[k] = v; // last wins on a dup key - harmless for a diagnostic
            }
            return m;
        }

        /// <summary>Comma-join with a per-clause char budget; overflow becomes "(+N more)".</summary>
        private static string JoinCapped(List<string> items)
        {
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            for (int i = 0; i < items.Count; i++)
            {
                string next = (shown > 0 ? ", " : "") + items[i];
                if (shown > 0 && sb.Length + next.Length > 220)
                    break;
                sb.Append(next);
                shown++;
            }
            if (shown < items.Count)
                sb.Append($" (+{items.Count - shown} more)");
            return sb.ToString();
        }

    }
}
