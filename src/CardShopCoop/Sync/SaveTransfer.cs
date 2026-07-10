using System;
using System.IO;
using System.Reflection;
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
            // stale slot-6 file from an earlier join would then pass the File.Exists check and
            // ship an OLD world to the joiner. A skip must be LOUD (the throw below), never stale.
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try
            {
                string gd = Application.persistentDataPath + "/savedGames_Release" + HostSnapshotSlot + ".gd";
                if (File.Exists(gd)) File.Delete(gd);
            }
            catch { }
            int prevSlot = gm.m_CurrentSaveLoadSlotSelectedIndex;
            try
            {
                gm.SaveGameData(HostSnapshotSlot); // synchronous: writes savedGames_Release6.json
            }
            finally
            {
                gm.m_CurrentSaveLoadSlotSelectedIndex = prevSlot; // undo SaveGameData's side effect
            }
            if (!File.Exists(path))
                throw new FileNotFoundException("Host snapshot save file missing after save", path);
            return File.ReadAllBytes(path);
        }

        /// <summary>Client: write the received world into the co-op slot and load it.</summary>
        public static void ApplyAndLoad(byte[] saveBytes)
        {
            string jsonPath = SlotPath(CoopSlot);
            string gdPath = Application.persistentDataPath + "/savedGames_Release" + CoopSlot + ".gd";
            File.WriteAllBytes(jsonPath, saveBytes);
            if (File.Exists(gdPath)) File.Delete(gdPath); // never fall back to a stale binary copy

            var gm = CSingleton<CGameManager>.Instance;
            gm.m_ForceNoCloudSaveLoad = true; // keep Steam cloud away from the borrowed world
            CoopPlugin.Log.LogInfo($"Coop save received ({saveBytes.Length / 1024} KB), loading world...");
            ForceLoadSlot(CoopSlot);
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
