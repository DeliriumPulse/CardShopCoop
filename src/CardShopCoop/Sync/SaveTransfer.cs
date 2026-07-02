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

        public static string SlotPath(int slot)
        {
            return Application.persistentDataPath + "/savedGames_Release" + slot + ".json";
        }

        /// <summary>Host: flush the live game into its current slot and return the save bytes.</summary>
        public static byte[] BuildHostPayload()
        {
            var gm = CSingleton<CGameManager>.Instance;
            int slot = gm.m_CurrentSaveLoadSlotSelectedIndex;
            gm.SaveGameData(slot); // synchronous: writes savedGames_Release{slot}.json
            string path = SlotPath(slot);
            if (!File.Exists(path))
                throw new FileNotFoundException("Host save file missing after save", path);
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
