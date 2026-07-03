using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop
{
    [BepInPlugin(Guid, Name, Version)]
    public class CoopPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.zwhit.cardshopcoop";
        public const string Name = "CardShopCoop";
        public const string Version = "1.0.11";

        public static ManualLogSource Log;

        public static ConfigEntry<int> Port;
        public static ConfigEntry<string> LastJoinIP;
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<float> SendRateHz;
        public static ConfigEntry<bool> AvatarsEnabled;
        public static ConfigEntry<KeyCode> UiToggleKey;
        public static ConfigEntry<KeyCode> EmoteKey;
        public static ConfigEntry<KeyCode> ServeKey;
        public static ConfigEntry<int> ClientWorldSlot;
        public static ConfigEntry<bool> AutoSyncCardDatabase;
        public static ConfigEntry<float> ServeReach;

        private void Awake()
        {
            Log = Logger;
            Util.FileLog.Init(Paths.GameRootPath);
            Logger.LogEvent += (_, e) => Util.FileLog.Write($"{e.Level,-7} {e.Data}");

            Port = Config.Bind("Network", "Port", 27886,
                "TCP port used for hosting. Both PCs' firewalls must allow the game on this port.");
            LastJoinIP = Config.Bind("Network", "LastJoinIP", "192.168.1.100",
                "IP address of the host PC (remembered after a successful join).");
            PlayerName = Config.Bind("Player", "Name", System.Environment.UserName,
                "Name shown above your head on the other player's screen.");
            SendRateHz = Config.Bind("Network", "SendRateHz", 15f,
                "How many position updates per second to send (8-20 is sensible).");
            if (Mathf.Approximately(SendRateHz.Value, 12f))
                SendRateHz.Value = 15f; // migrate configs saved by earlier builds
            AvatarsEnabled = Config.Bind("Player", "AvatarsEnabled", true,
                "Show the other player as a walking character in your shop.");
            UiToggleKey = Config.Bind("Keys", "UiToggleKey", KeyCode.F2,
                "Toggles the co-op window. (F3 is reserved for future co-op options.)");
            if (UiToggleKey.Value == KeyCode.F11)
                UiToggleKey.Value = KeyCode.F2; // migrate configs saved by early builds
            EmoteKey = Config.Bind("Keys", "EmoteKey", KeyCode.G,
                "Sends a wave emote that pops above your avatar.");
            ServeKey = Config.Bind("Keys", "ServeKey", KeyCode.V,
                "When JOINING: stand at the register and press this to serve the customer (scan items, take payment, give change).");
            ClientWorldSlot = Config.Bind("Network", "ClientWorldSlot", 7,
                "Save slot the co-op world uses when JOINING someone (your own slots 0-3 are never touched). On a PC dedicated to co-op you can set 0 for maximum mod-data fidelity.");
            AutoSyncCardDatabase = Config.Bind("Network", "AutoSyncCardDatabase", true,
                "When your modded-card ID registry (EPL enum_values.json) differs from the host's, automatically install the host's copy (yours is backed up beside it) so you only need to restart and rejoin. Set false to handle the file yourself.");
            ServeReach = Config.Bind("Player", "ServeReach", 1.6f,
                "How close (meters, to the counter's center) a JOINER must stand to serve the register or a trade customer. The counter itself is ~1m wide, so values below ~1.2 make it unreachable.");

            var harmony = new Harmony(Guid);
            Patches.GamePatches.ApplyAll(harmony);

            var go = new GameObject("CardShopCoop");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<CoopCore>();

            Log.LogInfo($"{Name} {Version} loaded. Press {UiToggleKey.Value} in-game to open the co-op window.");
        }
    }
}
