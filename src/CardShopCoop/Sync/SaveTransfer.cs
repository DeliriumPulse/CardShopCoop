using CardShopCoop.Net;
using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Join-time world sync: the host serializes its current shop with the game's own
    /// save pipeline and streams the JSON save file to the client. The client writes it
    /// into a dedicated slot (7) that the in-game save UI never shows, then boots it
    /// through the game's normal load path. Result: both players stand in the same shop,
    /// with the same shelves, stock, licenses and PTCGO expansions, without the client's
    /// own saves (slots 0-3) ever being touched.
    ///
    /// The in-memory half of that pipeline - SerializeInMemorySave on the host and the
    /// CSaveLoad.m_SavedGame injection in ApplyAndLoad - is an approach contributed by
    /// Jburne10 for the Game Pass build, where saves live in Xbox wgs containers invisible
    /// to the game process, so savedGames_Release&lt;slot&gt;.json is never written and never
    /// read.
    ///
    /// NEITHER HALF IS PLATFORM-GATED ANY MORE (1.0.39). Both used to key on
    /// PlatformProbe.SteamworksPresent, which answers a question about our dll-resolution
    /// environment rather than about the game's save backend: a Game Pass install carrying a
    /// stray com.rlabrecque.steamworks.net.dll answered TRUE, so hosting threw for a json the
    /// wgs backend never writes and joining loaded the guest's own world. What each half
    /// actually needed:
    ///  - HOST (BuildHostPayload): not "is this Steam" but "is the in-memory world CURRENT".
    ///    That is answered directly by the game's save-completion counter
    ///    (CPlayerData.m_SaveIndex/m_SaveCycle, bumped inside CGameData.SaveGameData upstream
    ///    of both backends) - see Net.PlatformProbe.TrySampleSaveCounter. A fresh slot file
    ///    still wins when there is one; the counter is what makes the in-memory fallback safe
    ///    when there isn't.
    ///  - GUEST (ApplyAndLoad): nothing at all - the gate is simply gone. The injection is a
    ///    no-op on the local-file backend (CGameManager.LoadData's first act is
    ///    CSaveLoad.Load(slot), which reassigns m_SavedGame from the file we just wrote,
    ///    BEFORE PropagateLoadData reads it) and it is the entire fix on the wgs backend. The
    ///    "we might dirty the guest's process-global" worry is separately covered by
    ///    GuestBorrowedWorld blocking every guest save until the title screen.
    /// </summary>
    public static class SaveTransfer
    {
        /// <summary>Slot the co-op world lives in on the client (config: ClientWorldSlot).</summary>
        public static int CoopSlot => CoopPlugin.ClientWorldSlot?.Value ?? 7;

        /// <summary>Throwaway slot the HOST snapshots its live world into at every join
        /// (mirror of the client's coop slot 7 convention). We must NOT force-save over the
        /// host's REAL slot: SaveGameData writes the on-disk file immediately, so snapshotting
        /// into the live slot baked whatever the world looked like at that instant - including
        /// a transient mid-join shelf-stock wipe (see WorldSync FIX D-a) - permanently into the
        /// host's real save. Slot 6 is outside the vanilla range (0 = autosave, 1-3 = manual),
        /// the in-game save UI never shows it, and the host never LOADS it, so it's safe to
        /// clobber on every join.</summary>
        public const int HostSnapshotSlot = 6;

        public static string SlotPath(int slot)
        {
            return Application.persistentDataPath + "/savedGames_Release" + slot + ".json";
        }

        /// <summary>Reads the game's in-memory save object (CSaveLoad.m_SavedGame) and returns
        /// it serialized exactly the way the game serializes it to disk. Null if unavailable.
        ///
        /// GAME PASS FALLBACK: on the Microsoft Store / Game Pass build the game does NOT write
        /// savedGames_Release&lt;slot&gt;.json at all - saves go to Xbox Game Save ("wgs") containers
        /// named GameData_&lt;slot&gt;, and MSIX redirection hides those from the game's own process,
        /// so no amount of file probing can find them. But the game keeps the whole save in
        /// memory as the static CSaveLoad.m_SavedGame, and its own writer is just
        /// JsonUtility.ToJson(m_SavedGame) (decompiled CSaveLoad.Save). Serializing that object
        /// ourselves therefore yields byte-identical content to the file the joiner expects,
        /// with no filesystem involved.
        ///
        /// The field is read through reflection and written back through fld.FieldType so this
        /// method never names CGameData: the Game Pass Assembly-CSharp is a different build and
        /// a renamed/reshaped save type must cost us this one path, not the whole type's load.
        /// Approach contributed by Jburne10.
        ///
        /// <paramref name="expectedSaveIndex"/> is CPlayerData.m_SaveIndex as it stood right
        /// AFTER the save (or -1 when the counter oracle could not answer). CSaveLoad.Save
        /// re-points m_SavedGame at CGameData.instance as its first statement, so on the
        /// local-file backend the two always agree - but the Gamecore writer is not obliged to
        /// do that, and a m_SavedGame left behind by an earlier save or load carries the OLD
        /// index. When it does not match we serialize CGameData.instance instead, which is the
        /// exact object CSaveLoad.Save would have written. Reached through fld.FieldType, so
        /// CGameData still goes unnamed here.
        ///
        /// <paramref name="identity"/> receives the shipped world's day + shop, read off the
        /// very object we serialized (see <see cref="DescribeWorldObject"/>), or null when this
        /// build does not expose those fields. Taken HERE rather than recomputed by the caller
        /// because this is the only scope that holds the object after the m_SavedGame ->
        /// CGameData.instance redirect above - describing anything else could name a world we
        /// did not actually send.</summary>
        private static byte[] SerializeInMemorySave(int expectedSaveIndex, out string identity)
        {
            identity = null;
            try
            {
                const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var fld = typeof(CSaveLoad).GetField("m_SavedGame", F);
                if (fld == null)
                    return null;
                object saved = fld.GetValue(null);

                if (saved == null || !MatchesSaveIndex(saved, expectedSaveIndex))
                {
                    object live = null;
                    try
                    {
                        live = fld.FieldType.GetField("instance", F)?.GetValue(null);
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                    if (live != null)
                    {
                        if (saved != null)
                            CoopPlugin.Log.LogInfo("coop: m_SavedGame is not the world that was just saved - serializing CGameData.instance instead");
                        saved = live;
                    }
                }
                if (saved == null)
                    return null;
                identity = DescribeWorldObject(saved);

                string json = JsonUtility.ToJson(saved);
                // an empty/degenerate serialization means the object is not a real world -
                // shipping "{}" to the joiner would boot it into an empty shop
                if (string.IsNullOrEmpty(json) || json.Length < 32 || json == "{}")
                {
                    identity = null;
                    return null;
                }
                return new UTF8Encoding(false).GetBytes(json); // no BOM: the game's writer emits none
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("coop: in-memory save serialization failed: " + e.Message);
                identity = null;
                return null;
            }
        }

        // =====================================================================
        // SHIPPED-WORLD IDENTITY (1.0.39). One log line, on whichever path actually
        // produced the bytes, naming WHAT went over the wire.
        //
        // WHY IT EARNS ITS PLACE: every failure this file guards against - a stale
        // slot-6 file that survived a failed delete, a m_SavedGame left behind by an
        // earlier save, a SaveGameData that silently bailed on one of its four guards -
        // fails by shipping a PLAUSIBLE world rather than by shipping nothing. A byte
        // count cannot tell those apart from success; "day 52" can. With this line the
        // host's log and the guest's shop can be compared after the fact, by two people
        // in a support thread, without either of them re-running the join.
        //
        // FIELD NAMES, from the decompile (decompiled/CGameData.cs): CGameData.m_CurrentDay
        // (int, line 242) and CGameData.m_PlayerName (string, line 224). THERE IS NO
        // SHOP-NAME FIELD IN THE SAVE - the shop is the player's shop, which is exactly why
        // the game's own lobby name is "<PlayerName>'s shop" - so m_PlayerName IS the shop
        // identity here. CPlayerData carries no separate one either; its m_PlayerName is a
        // private static mirror (decompiled/CPlayerData.cs line 304).
        //
        // Both readers are null-safe, never throw, and never name CGameData - the same rule
        // the rest of this path lives by. A reshaped build costs us the log line and nothing
        // else: callers fall back to the byte size alone.
        // =====================================================================

        /// <summary>Identity read off the in-memory save OBJECT by reflection. Null when the
        /// fields are absent.</summary>
        private static string DescribeWorldObject(object saved)
        {
            if (saved == null)
                return null;
            try
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var t = saved.GetType();
                object day = t.GetField("m_CurrentDay", F)?.GetValue(saved);
                object who = t.GetField("m_PlayerName", F)?.GetValue(saved);
                return Describe(day is int d ? d : (int?)null, who as string);
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        /// <summary>Identity scanned straight out of the save JSON - the slot-file path, where
        /// we hold bytes and no object.
        ///
        /// A DELIBERATE INDEX-OF SCAN, NOT A DESERIALIZE. Running a ~400 KB world back through
        /// JsonUtility to print two fields would parse the entire save a second time on every
        /// join, for a log line; this walks it once looking for two keys. The one real cost is
        /// decoding the bytes to a string, and that is unavoidable rather than lazy: both
        /// fields are declared AFTER CGameData's big lists, so they sit LATE in the document
        /// and a cheap prefix-only decode would simply never reach them.
        ///
        /// THE FIRST HIT IS THE RIGHT HIT, and that is a checked property, not a hope: across
        /// the whole 608-file decompile m_CurrentDay is declared ONLY on CGameData, and
        /// m_PlayerName only on CGameData and on CPlayerData - whose copy is a private STATIC,
        /// which JsonUtility never serializes. No nested save-data type carries either name, so
        /// no shelf, customer or worker entry can shadow the top-level field.
        ///
        /// Returns null when either key is missing or the shape is unexpected; never throws.</summary>
        private static string DescribeWorldJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                int? day = null;
                const string dayKey = "\"m_CurrentDay\":";
                int i = json.IndexOf(dayKey, StringComparison.Ordinal);
                if (i >= 0)
                {
                    i += dayKey.Length;
                    int j = i;
                    while (j < json.Length && (json[j] == '-' || (json[j] >= '0' && json[j] <= '9')))
                        j++;
                    if (j > i && int.TryParse(json.Substring(i, j - i), out int d))
                        day = d;
                }

                string who = null;
                // require the opening quote: on a reshaped build where this is no longer a
                // string we simply find nothing, rather than mis-parsing whatever is there
                const string nameKey = "\"m_PlayerName\":\"";
                int k = json.IndexOf(nameKey, StringComparison.Ordinal);
                if (k >= 0)
                {
                    k += nameKey.Length;
                    // Stop at the first UNESCAPED quote. A shop name may legitimately contain
                    // a quote, which JsonUtility writes escaped, and a plain IndexOf would
                    // truncate the name there.
                    int end = k;
                    while (end < json.Length && !(json[end] == '"' && json[end - 1] != '\\'))
                        end++;
                    if (end < json.Length)
                        who = json.Substring(k, end - k); // no closing quote = truncated json, take nothing
                }

                return Describe(day, who);
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        /// <summary>Format the two fields, tolerating either being unavailable. Null when
        /// neither could be read, which is the caller's signal to log the byte size alone.
        /// The name is length-capped: it is player-supplied text going into a shared log, and
        /// a pathological one must not bury the rest of the join.</summary>
        private static string Describe(int? day, string shop)
        {
            bool hasShop = !string.IsNullOrEmpty(shop);
            if (day == null && !hasShop)
                return null;
            if (hasShop && shop.Length > 64)
                shop = shop.Substring(0, 64) + "...";
            if (!hasShop)
                return "day " + day.Value;
            if (day == null)
                return "shop \"" + shop + "\"";
            return "day " + day.Value + ", shop \"" + shop + "\"";
        }

        /// <summary>Does this save object carry the save index the counter oracle just
        /// observed? Anything we cannot check - unknown expected index, missing field,
        /// reflection failure - answers TRUE: this test exists to REDIRECT to a better object
        /// when it has positive evidence of a stale one, never to reject the only object we
        /// have on a build it cannot read.</summary>
        private static bool MatchesSaveIndex(object saved, int expectedSaveIndex)
        {
            if (expectedSaveIndex < 0)
                return true;
            try
            {
                var f = saved.GetType().GetField("m_SaveIndex",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null)
                    return true;
                return (int)f.GetValue(saved) == expectedSaveIndex;
            }
            catch (System.Exception e) { Swallow.Log(e); return true; }
        }

        /// <summary>Host: flush the live game into the THROWAWAY snapshot slot (never the host's
        /// real slot) and return the save bytes. CGameManager.SaveGameData derives the file path
        /// from the slot ARG (verified in the decompiled source - slot 6 writes
        /// savedGames_Release6.json), but it also overwrites m_CurrentSaveLoadSlotSelectedIndex as
        /// a side effect; that field feeds the game's load/quit paths, so we capture and RESTORE
        /// it in a finally to keep the host's notion of "current slot" from drifting to 6.</summary>
        public static byte[] BuildHostPayload()
        {
            var gm = CSingleton<CGameManager>.Instance;
            string path = SlotPath(HostSnapshotSlot);
            // delete the PREVIOUS join's snapshot first: SaveGameData silently bails on any of
            // its guards (loading error, mid scene-transition, day-report screen...), and a
            // stale slot-6 file from an earlier join would then be mistaken for this join's
            // snapshot and ship an OLD world to the joiner. A skip must be LOUD (the throw
            // below), never stale. Best effort only - the timestamp check further down is what
            // makes the guarantee, because this delete is allowed to fail silently.
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            try
            {
                string gd = Application.persistentDataPath + "/savedGames_Release" + HostSnapshotSlot + ".gd";
                if (File.Exists(gd))
                    File.Delete(gd);
            }
            catch (System.Exception e) { Swallow.Log(e); }
            int prevSlot = gm.m_CurrentSaveLoadSlotSelectedIndex;
            // FRESHNESS CLOCK. The two deletes above swallow their failures on purpose (an
            // antivirus or cloud-sync client can hold the file open for a moment, and a
            // transient lock must not abort a join) - which means a stale slot-6 file can
            // SURVIVE them, and a bare File.Exists below would then hand the joiner an old
            // world exactly as if nothing had gone wrong. So stamp the clock here, before the
            // save, and require the file to be at least this new: existence alone proves the
            // save COMPLETED, but only the timestamp proves it completed NOW.
            DateTime t0 = DateTime.UtcNow;
            // SAVE-COMPLETION COUNTER, sampled either side of SaveGameData. This is the
            // freshness clock's counterpart for builds whose backend writes no file we can
            // stat: CGameData.SaveGameData bumps CPlayerData.m_SaveIndex (wrapping into
            // m_SaveCycle) UPSTREAM of the backend call, so a bump proves the save ran all the
            // way through the four guards on EITHER backend, and no bump proves it bailed.
            // Sampled before the try so a throw inside SaveGameData still leaves us a baseline.
            int idx0, cyc0;
            bool counterBefore = Net.PlatformProbe.TrySampleSaveCounter(out idx0, out cyc0);
            try
            {
                gm.SaveGameData(HostSnapshotSlot); // synchronous: writes savedGames_Release6.json
            }
            catch (Exception e)
            {
                // Some builds throw inside SaveGameData's save-slot UI refresh for this
                // out-of-band slot (the save-UI list has no button for slot 6). The world data
                // is written before that point, so log and let the checks below decide.
                CoopPlugin.Log.LogWarning("coop: SaveGameData(" + HostSnapshotSlot + ") threw: " + e.Message);
            }
            finally
            {
                gm.m_CurrentSaveLoadSlotSelectedIndex = prevSlot; // undo SaveGameData's side effect
            }

            // ORDER OF PREFERENCE - deliberately the reverse of the upstream Game Pass patch,
            // and the one place we deviate from it. A FRESH slot file wins whenever there is
            // one: CSaveLoad.Save writes a temp json, validates it by round-tripping it back
            // through JsonUtility, and only THEN moves it onto savedGames_Release6.json - so a
            // file whose timestamp is on OUR side of t0 is PROOF that the save ran all the way
            // to completion, just now. The in-memory object carries no such proof:
            // CGameManager.SaveGameData bails silently on any of its four guards (title screen,
            // loading error, no shelf data, savefile still loading), and CSaveLoad.m_SavedGame
            // then still holds whatever the previous save or load left in it - an OLD world that
            // would ship to the joiner with no error at all. A skip must be LOUD, never stale.
            //
            // A file that exists but is OLD is treated as ABSENT (a delete that quietly failed
            // - see the clock above), which drops through to the counter-gated fallback and,
            // when the counter says the save never ran, to the throw. The one second of slack is for filesystem timestamp
            // granularity and clock skew only; it is orders of magnitude shorter than the gap
            // between two joins, so it can never re-admit a previous session's snapshot.
            bool fresh;
            try
            {
                fresh = File.Exists(path) && File.GetLastWriteTimeUtc(path) >= t0.AddSeconds(-1);
            }
            catch { fresh = false; } // unreadable metadata is not proof of anything: treat as absent
            if (fresh)
            {
                byte[] bytes = File.ReadAllBytes(path);
                // Identity line (see DescribeWorldJson): both paths out of this method now say
                // WHICH world they shipped, not just how big it was. Both start "coop: shipping"
                // so a support log has exactly one greppable line for it, with the tail naming
                // the path that produced it.
                string id = null;
                try
                {
                    id = DescribeWorldJson(new UTF8Encoding(false).GetString(bytes));
                }
                catch (System.Exception e) { Swallow.Log(e); } // a log line is never worth failing a join over
                CoopPlugin.Log.LogInfo("coop: shipping world from slot file" +
                    (id != null ? " - " + id : "") + $" ({bytes.Length / 1024} KB)");
                return bytes;
            }

            // ---- fallback: in-memory path ----
            // GATED ON THE GAME'S SAVE-COMPLETION COUNTER, NOT ON A PLATFORM TEST. No fresh
            // file still has two completely different meanings, and only one is survivable -
            // but "which one" is a question about whether SaveGameData RAN, not about which
            // store sold the game, and the counter answers it head-on:
            //  - counter BUMPED: the save ran all the way through its four guards, so
            //    CSaveLoad.Save has already re-pointed the world object at CGameData.instance
            //    and the in-memory world is CURRENT. Either the backend writes no local json
            //    (Game Pass wgs), or it does and its validate/move loop failed - and in that
            //    second case the world in memory is still correct, so 1.0.38's throw was
            //    strictly worse than shipping it. Accept.
            //  - counter DID NOT bump: SaveGameData silently bailed on one of its guards, and
            //    the in-memory object is precisely the stale world those guards exist to stop
            //    us from shipping. Falling back would replace a loud, fixable error with a
            //    joiner standing in an old or empty shop. Throw.
            //  - counter fields NOT FOUND (unknown build only): fall through to the Gamecore
            //    TYPE probe, which is derived from the game assembly rather than from our dll
            //    environment. NEVER PlatformProbe.SteamworksPresent - a stray
            //    com.rlabrecque.steamworks.net.dll in a Game Pass install is exactly the field
            //    failure this rewrite fixes.
            int idx1, cyc1;
            bool counterAfter = Net.PlatformProbe.TrySampleSaveCounter(out idx1, out cyc1);
            bool counterKnown = counterBefore && counterAfter;
            bool saveRan = counterKnown && (idx1 != idx0 || cyc1 != cyc0);
            bool acceptInMemory = saveRan || (!counterKnown && Net.PlatformProbe.GamePassBuild);
            if (acceptInMemory)
            {
                string memId;
                byte[] mem = SerializeInMemorySave(saveRan ? idx1 : -1, out memId);
                if (mem != null)
                {
                    // The identity matters MOST here. This is the path with no filesystem
                    // proof behind it - the counter says a save ran, but only the day and the
                    // shop name say which world the object we serialized actually is.
                    CoopPlugin.Log.LogInfo("coop: shipping in-memory world" +
                        (memId != null ? " - " + memId : "") +
                        $" ({mem.Length / 1024} KB, " +
                        (saveRan ? "save-completion counter advanced" : "no counter on this build, Gamecore save type present") + ")");
                    return mem;
                }
            }

            throw new FileNotFoundException(
                acceptInMemory
                ? "Host snapshot failed on BOTH paths: the game wrote no fresh slot file, AND the " +
                  "in-memory world (CSaveLoad.m_SavedGame / CGameData.instance) could not be read or " +
                  "was empty. " +
                  (saveRan
                   ? "The game's save-completion counter (CPlayerData.m_SaveIndex/m_SaveCycle) DID " +
                     "advance, so the save itself ran - it is the world object we could not serialize. "
                   : "This build ships a Gamecore save type, so it writes Xbox wgs containers rather " +
                     "than local json and the in-memory path is the only one that can work here. ") +
                  "Load into your shop fully, then retry."
                : counterKnown
                ? "Host snapshot failed: the game wrote no fresh slot file AND its save-completion " +
                  "counter (CPlayerData.m_SaveIndex/m_SaveCycle) did not advance, which is positive " +
                  "proof SaveGameData bailed on one of its guards (title screen, loading error, no " +
                  "shelf data, or the savefile was still loading). The in-memory save object is " +
                  "deliberately not used here, because after a skipped save it still holds the " +
                  "PREVIOUS world and would ship it to the joiner with no error at all. Load into " +
                  "your shop fully, then retry."
                : "Host snapshot failed: the game wrote no fresh slot file (SaveGameData skipped - " +
                  "title screen, loading error, no shelf data, or the savefile was still loading), and " +
                  "this build exposes neither the save-completion counter (CPlayerData.m_SaveIndex) " +
                  "nor a Gamecore save type, so nothing available here can prove the save ran. The " +
                  "in-memory save object is deliberately not used without that proof, because after a " +
                  "skipped save it still holds the PREVIOUS world and would ship it to the joiner with " +
                  "no error at all. Load into your shop fully, then retry.", path);
        }

        /// <summary>Apply filesystem changes away from Unity's update thread. The object
        /// injection and scene transition are deliberately marshalled back to the main thread:
        /// JsonUtility and CGameManager are Unity/game APIs and are not thread-safe.</summary>
        public static void ApplyAndLoadAsync(byte[] saveBytes, int sessionGen,
            Action completed, Action<Exception> failed)
        {
            if (saveBytes == null || saveBytes.Length == 0)
                throw new ArgumentException("Received save payload is empty", nameof(saveBytes));

            string root = Application.persistentDataPath;
            string basePath = Path.Combine(root, "savedGames_Release" + CoopSlot);
            string slotPath = basePath + ".json";
            string backupPath = Path.Combine(root, "savedGames_ReleaseBackupFile" + CoopSlot + ".json");
            new System.Threading.Thread(() =>
            {
                try
                {
                    lock (CoopCore.JoinTransferLock)
                    {
                        if (!CoopCore.IsSessionGeneration(sessionGen))
                        {
                            CoopPlugin.Log.LogInfo("coop: stale save apply discarded before disk write");
                            return;
                        }
                        TryDelete(basePath + ".gd");
                        TryDelete(slotPath);
                        TryDelete(backupPath);
                        if (File.Exists(basePath + ".gd") || File.Exists(slotPath) || File.Exists(backupPath))
                            throw new IOException("A previous co-op save could not be cleared");
                        File.WriteAllBytes(slotPath, saveBytes);
                    }
                    if (!CoopCore.IsSessionGeneration(sessionGen))
                    {
                        CoopPlugin.Log.LogInfo("coop: stale save apply discarded before load");
                        return;
                    }
                    CoopCore.TryEnqueueMainThread(() =>
                    {
                        if (!CoopCore.IsSessionGeneration(sessionGen))
                        {
                            CoopPlugin.Log.LogInfo("coop: stale save load callback discarded");
                            return;
                        }
                        try
                        {
                            InjectAndForceLoad(saveBytes);
                            completed?.Invoke();
                        }
                        catch (Exception e)
                        {
                            CoopPlugin.Log.LogError("coop: received world main-thread apply failed: " + e);
                            failed?.Invoke(e);
                        }
                    });
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError("coop: save apply worker failed: " + e);
                    if (CoopCore.IsSessionGeneration(sessionGen))
                        CoopCore.TryEnqueueMainThread(() =>
                        {
                            if (CoopCore.IsSessionGeneration(sessionGen))
                                failed?.Invoke(e);
                        });
                }
            })
            {
                IsBackground = true,
                Name = "CoopSaveApply"
            }.Start();
        }

        private static void InjectAndForceLoad(byte[] saveBytes)
        {
            var gm = CSingleton<CGameManager>.Instance;
            gm.m_ForceNoCloudSaveLoad = true;
            bool injected = false;
            string json = new UTF8Encoding(false).GetString(saveBytes)
                .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (json.StartsWith("{", StringComparison.Ordinal))
            {
                var fld = typeof(CSaveLoad).GetField("m_SavedGame",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (fld != null)
                {
                    object world = JsonUtility.FromJson(json, fld.FieldType);
                    if (world != null)
                    {
                        fld.SetValue(null, world);
                        injected = true;
                    }
                }
            }
            CoopPlugin.Log.LogInfo($"Coop save received ({saveBytes.Length / 1024} KB){(injected ? " [in-memory]" : "")}, loading world...");
            ForceLoadSlot(CoopSlot);
        }

        /// <summary>Client: apply the received world into the co-op slot and load it.
        ///
        /// GAME PASS FALLBACK: writing savedGames_Release&lt;CoopSlot&gt;.json is not enough on the
        /// Microsoft Store build - the game never reads that file there (its saves live in Xbox
        /// wgs containers), so the subsequent load silently kept the client's own world. We
        /// therefore ALSO deserialize the received JSON straight into CSaveLoad.m_SavedGame,
        /// which is the object CGameManager.LoadData propagates into the live CGameData.
        ///
        /// UNCONDITIONAL SINCE 1.0.39 - THERE IS NO ORACLE HERE, AND THERE MUST NOT BE ONE.
        /// This used to be gated on PlatformProbe.SteamworksPresent, mirroring BuildHostPayload,
        /// and a stray com.rlabrecque.steamworks.net.dll in a Game Pass install turned that gate
        /// into "skip the only thing that works here" - the guest loaded its own world and the
        /// join looked like it had succeeded. The gate is not replaced with a better test
        /// because none is needed: where the local-file backend is live the injection is
        /// PROVABLY overwritten before anything reads it (we write savedGames_Release&lt;slot&gt;.json
        /// below, then ForceLoadSlot -> LoadMainLevelAsync -> CGameManager.LoadData, whose FIRST
        /// act is CSaveLoad.Load(slot) reassigning m_SavedGame from that very file, before
        /// PropagateLoadData ever reads it), and where the wgs backend is live it is the entire
        /// fix. So it is a no-op on one backend and the fix on the other - zero oracle required.
        /// The old "we might dirty the guest's process-global" worry is additionally covered by
        /// GuestBorrowedWorld, which blocks every guest save until the title screen.
        ///
        /// The file write is retained on BOTH backends (harmless where it isn't read) and
        /// wrapped in its own try/catch so that a permission-restricted Packages tree (the Game
        /// Pass persistentDataPath) degrades to in-memory-only instead of aborting the join.
        /// Approach contributed by Jburne10.</summary>
        public static void ApplyAndLoad(byte[] saveBytes)
        {
            var gm = CSingleton<CGameManager>.Instance;
            gm.m_ForceNoCloudSaveLoad = true; // keep Steam/Xbox cloud away from the borrowed world

            bool injected = false;
            try
            {
                // the host may have serialized from memory (no BOM) or shipped a file the
                // game wrote; trim either shape before the sanity check
                string json = new UTF8Encoding(false).GetString(saveBytes)
                                                     .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                if (json.StartsWith("{", StringComparison.Ordinal)) // never hand junk to FromJson
                {
                    var fld = typeof(CSaveLoad).GetField("m_SavedGame",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fld != null)
                    {
                        // FromJson(json, fld.FieldType), not FromJson<CGameData>: the save
                        // type is resolved from the running build, never named here
                        object world = JsonUtility.FromJson(json, fld.FieldType);
                        if (world != null)
                        {
                            fld.SetValue(null, world);
                            injected = true;
                        }
                        else
                            CoopPlugin.Log.LogWarning("coop: FromJson returned null for the received world");
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("coop: injecting received world failed: " + e.Message); }

            // CLEAR EVERY LOADABLE FORM OF THE CO-OP SLOT FIRST - all three, in one try, BEFORE
            // the write below. If the write then throws (a restricted Packages tree), the slot
            // must hold NOTHING: a failure that boots the joiner into an empty slot is loud and
            // obvious, while a failure that boots them into the PREVIOUS join's world is silent
            // and wrong. The game gives the slot three chances to be stale:
            //   .gd   - CSaveLoad.Load deserializes savedGames_Release<slot>.gd when the json is
            //           missing, so a .gd left by an earlier session is a complete old world.
            //   .json - the file we are about to overwrite. Deleting it first means a failed
            //           write leaves no file at all instead of the one already sitting there.
            //   savedGames_ReleaseBackupFile<slot>.json - the game's LoadBackupData reads this
            //           when the normal Load fails, which is exactly the state a failed write
            //           puts us in. It is the last door into a previous join's world.
            //
            // ONE try PER FILE, not one around all three. An antivirus or cloud-sync client
            // holding ANY of them open threw out of the shared block and silently skipped the
            // rest - and the two doors most likely to be left standing that way (.gd and the
            // backup json) are precisely the ones the game falls back to when the load of the
            // file we DID replace goes wrong. Each delete is independent, so each gets its own
            // chance to succeed and its own warning when it doesn't.
            string basePath = Application.persistentDataPath + "/savedGames_Release" + CoopSlot;
            TryDelete(basePath + ".gd");
            TryDelete(SlotPath(CoopSlot));
            TryDelete(Application.persistentDataPath + "/savedGames_ReleaseBackupFile" + CoopSlot + ".json");

            // A failed write is NOT fatal: on Game Pass the injected in-memory world above is
            // what the load path reads anyway, so we degrade to in-memory-only and carry on.
            try
            {
                File.WriteAllBytes(SlotPath(CoopSlot), saveBytes);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("coop: could not write co-op slot file: " + e.Message); }

            CoopPlugin.Log.LogInfo($"Coop save received ({saveBytes.Length / 1024} KB){(injected ? " [in-memory]" : "")}, loading world...");
            ForceLoadSlot(CoopSlot);
        }

        /// <summary>Delete one file if it is there, and let a failure be exactly that one file's
        /// failure. Never throws: every caller is mid-join, where a locked file is a degraded
        /// outcome to warn about, not a reason to abandon the join.</summary>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("coop: could not clear " + Path.GetFileName(path) + ": " + e.Message);
            }
        }

        /// <summary>Drive the game's own title->shop load path for an arbitrary slot.</summary>
        public static void ForceLoadSlot(int slot)
        {
            var gm = CSingleton<CGameManager>.Instance;
            gm.m_CurrentSaveLoadSlotSelectedIndex = slot;

            // The load-on-scene-enter path only runs while m_InitLoaded is false.
            var initLoaded = typeof(CGameManager).GetField("m_InitLoaded",
                BindingFlags.NonPublic | BindingFlags.Static);
            initLoaded?.SetValue(null, false);

            gm.LoadMainLevelAsync("Start", slot);
        }
    }
}
