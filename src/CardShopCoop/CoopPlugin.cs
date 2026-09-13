using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using System.Reflection;
using UnityEngine;

namespace CardShopCoop
{
    /// <summary>How loudly a graded-album divergence is announced ON SCREEN.
    /// The LOG is never gated by this - a support log has to be complete whatever the player
    /// chose, and the whole point of the setting is to quiet the screen without going blind.</summary>
    public enum GradedAlertMode
    {
        Always, OncePerSession, Never
    }

    [BepInPlugin(Guid, Name, Version)]
    // SOFT dependency on Grading Overhaul: it changes NOTHING when GO is absent, and when GO is
    // present it guarantees GO is loaded before our Awake. That ordering is load-bearing now that
    // Sync/GradingSync patches GO's OWN day-start prefix and resolves GO's submit validator at
    // patch time (see TryPatchGoDayStart) - both are one-shot reflective lookups, so a guest that
    // happened to sort ahead of GO in the chainloader would silently lose the guard that stops it
    // minting certificate numbers. Guid copied from GO's own BepInPlugin (decompiled-grading
    // :16912), which is also the Harmony owner id used in the `before` array over there.
    [BepInDependency("munch.gradingoverhaul", BepInDependency.DependencyFlags.SoftDependency)]
    public partial class CoopPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.zwhit.cardshopcoop";
        public const string Name = "CardShopCoop";
        public static ManualLogSource Log;

        public static ConfigEntry<int> Port;
        public static ConfigEntry<string> LastJoinIP;
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<float> SendRateHz;
        public static ConfigEntry<bool> AvatarsEnabled;
        public static ConfigEntry<bool> AllowNsfw;
        public static ConfigEntry<KeyCode> UiToggleKey;
        public static ConfigEntry<KeyCode> EmoteKey;
        public static ConfigEntry<int> ClientWorldSlot;
        public static ConfigEntry<bool> AutoSyncCardDatabase;
        public static ConfigEntry<float> ServeReach;
        public static ConfigEntry<bool> AllowCrossBuildJoin;
        public static ConfigEntry<bool> AutoPortForward;
        public static ConfigEntry<bool> AutoLanPassword;
        public static ConfigEntry<GradedAlertMode> GradedDriftAlert;
        public static ConfigEntry<bool> BoxSyncDebug;
        public static ConfigEntry<bool> PerfDebug;
        public static ConfigEntry<int> ArtificialLagMs;
        public static ConfigEntry<int> ArtificialJitterMs;

        private void Awake()
        {
            Log = Logger;
            try
            {
                Assembly.Load("Newtonsoft.Json");
                Logger.LogInfo("network payload serializer: Newtonsoft.Json");
            }
            catch (Exception e)
            {
                Logger.LogError("network payload serializer unavailable: Newtonsoft.Json.dll is required (" + e.Message + ")");
            }
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
            AllowNsfw = Config.Bind("Player", "AllowNsfw", false,
                "Allow nude/NSFW character appearances. When off, new characters get a random clothed preset, the Nude wardrobe option is hidden, and fully nude players are shown in the game's random clothed customer look. When on, you may choose Nude and you will see other players who chose it.");
            UiToggleKey = Config.Bind("Keys", "UiToggleKey", KeyCode.F2,
                "Toggles the co-op window. (F3 is reserved for future co-op options.)");
            if (UiToggleKey.Value == KeyCode.F11)
                UiToggleKey.Value = KeyCode.F2; // migrate configs saved by early builds
            EmoteKey = Config.Bind("Keys", "EmoteKey", KeyCode.G,
                "Sends a wave emote that pops above your avatar.");
            ClientWorldSlot = Config.Bind("Network", "ClientWorldSlot", 7,
                "Save slot the co-op world uses when JOINING someone (your own slots 0-3 are never touched). On a PC dedicated to co-op you can set 0 for maximum mod-data fidelity.");
            AutoSyncCardDatabase = Config.Bind("Network", "AutoSyncCardDatabase", true,
                "When your modded-card ID registry (EPL enum_values.json) differs from the host's, automatically install the host's copy (yours is backed up beside it) so you only need to restart and rejoin. Set false to handle the file yourself.");
            ServeReach = Config.Bind("Player", "ServeReach", 1.6f,
                "How close (meters, to the counter's center) a JOINER must stand to answer a counter trade customer. The counter itself is ~1m wide, so values below ~1.2 make it unreachable.");
            AllowCrossBuildJoin = Config.Bind("Network", "AllowCrossBuildJoin", false,
                "Let players join even when the GAME build fingerprint (game version / Unity version) differs from the host's. Dangerous: two different game builds can corrupt each other's saves. Only enable for supervised testing of Steam <-> Game Pass cross-play.");
            AutoPortForward = Config.Bind("Network", "AutoPortForward", true,
                "While you are LAN-hosting, ask your router over UPnP to open the co-op port so a friend outside your house can join with an invite code - and remove that opening again when you stop hosting. Turn it off if you forward the port yourself, or if you would rather nothing touched the router. Either way the invite code still works inside your own house.");
            AutoLanPassword = Config.Bind("Network", "AutoLanPassword", true,
                "Hosting via LAN generates a random session password. It is baked into the invite code (so a friend using the code notices nothing), shown in the host panel for a friend typing your IP by hand, and checked exactly like the Steam lobby password. Leave this on: the port your router opens for you is a door into your game, and this is the lock on it.");
            GradedDriftAlert = Config.Bind("Graded", "DriftAlert", GradedAlertMode.Always,
                "How loudly to announce that your graded albums have drifted apart. THIS CONTROLS THE HOST'S SCREEN. While you are HOSTING it decides both your own on-screen line and the heads-up sent to the joiner. While you are JOINING it does nothing at all: the host's setting alone decides whether you get that heads-up, because the drift is only ever announced from the host's side. Always: announce every check that finds a difference. OncePerSession: say it once per player per session and then stay quiet - including for a later, bigger difference. Never: never put it on screen at all. The log records every check whichever you pick and on both PCs, so a support log stays complete whatever you choose; this only controls the screen, and it never changes what the co-op panel's adopt button offers.");
            BoxSyncDebug = Config.Bind("Diagnostics", "BoxSyncDebug", false,
                "Log detailed box-sync activity: each possession report a client sends, each update the host accepts or rejects, and each box the client adopts or spawns. Verbose - enable temporarily (then restart) to diagnose boxes that will not pick up, move, or hide in sync.");
            PerfDebug = Config.Bind("Diagnostics", "PerfDebug", false,
                "Log any per-frame sync stage that takes longer than 5 ms (rate-limited to one line per stage per 2 s). Enable temporarily to find lag spikes; it does not change gameplay.");
            ArtificialLagMs = Config.Bind("Diagnostics", "ArtificialLagMs", 0,
                "TESTING ONLY. Adds this many milliseconds of latency to every network message you RECEIVE. Set the same value on both PCs for symmetric lag (each hop adds one delay, so a round trip is roughly twice the value). 0 disables it.");
            ArtificialJitterMs = Config.Bind("Diagnostics", "ArtificialJitterMs", 0,
                "TESTING ONLY. Randomly varies each received message's artificial latency by up to this many milliseconds, plus or minus, so the connection feels unstable. 0 is a steady delay. Only meaningful while ArtificialLagMs is above 0.");

            // PLATFORM LINE FIRST, ABOVE EVERYTHING THAT CAN FAIL. This is the line a Game
            // Pass player (or a support thread) is told to look for, and it is most useful
            // precisely in the session where CoopCore fails to load - so it must not sit
            // downstream of the very failure it explains.
            //
            // IT REPORTS ONLY WHAT IT ACTUALLY KNOWS (1.0.39). PlatformProbe answers exactly
            // one question - "can this process bind the Steamworks wrapper?" - and the field
            // proved that is NOT the same question as "will Steam work here": a Game Pass
            // install carrying a stray com.rlabrecque.steamworks.net.dll answers TRUE, and the
            // old wording then PROMISED that player "Steam lobbies, invites and P2P available"
            // on a build where Steam can never run. So this line is now a neutral statement of
            // fact about the assembly, and the promise moved to CoopCore.Awake - the first
            // point that knows whether the bridge actually came up. Do not put it back here:
            // nothing at this point in startup is entitled to make it.
            if (!Net.PlatformProbe.SteamworksPresent)
                Log.LogInfo("Steamworks assembly not detected - LAN and direct IP only (a HarmonyX ReflectionTypeLoadException warning naming Steamworks types may appear when any mod - including this one - enumerates loaded types; it is EXPECTED on this build and harmless)");
            else
                Log.LogInfo("Steamworks assembly detected.");

            // SECOND LINE, ON PURPOSE. The line above answers "can Steam UI exist here"; this
            // one answers "where do this game's saves live", and 1.0.38 shipped a field report
            // where the first was true and the second was not (a stray
            // com.rlabrecque.steamworks.net.dll in a Game Pass install). SaveTransfer no longer
            // infers one from the other, and neither should the log.
            Log.LogInfo("save backend: " + Net.PlatformProbe.SaveBackendDescription +
                        " - decided from the game's own save-completion counter at join time, never from Steamworks presence.");

            // ORDER MATTERS, AND NOT THE WAY YOU'D EXPECT. CoopCore is added FIRST and the
            // Harmony patches only go in if that succeeded.
            //
            // Every patch body reads CoopCore statics (Role, GuestBorrowedWorld), so the
            // patches inherit CoopCore's type-load fate. With the old order - patch first,
            // AddComponent second - a CoopCore that failed to load left the patches LIVE
            // and unbacked: BepInEx catches the AddComponent failure and logs "Error loading
            // [CardShopCoop]", but every prefix then throws at first JIT inside a patched
            // GAME method (CGameManager.SaveGameData, CustomerManager.Update,
            // CPlayerData.AddCard...). A dead mod is a nuisance; a dead mod that breaks
            // saving and customers is a broken GAME. This way the failure degrades to
            // clean vanilla.
            var go = new GameObject("CardShopCoop");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;

            bool coreLoaded;
            try
            {
                AttachCore(go);
                coreLoaded = true;
            }
            catch (System.Exception e)
            {
                // TypeLoadException lands here when a dependency of CoopCore is missing on
                // this platform. Say so loudly - this is the line that explains an otherwise
                // silent "co-op window won't open".
                coreLoaded = false;
                Log.LogError($"{Name} {Version}: CoopCore FAILED TO LOAD - co-op is disabled this session and NO game patches were installed (the game runs exactly as vanilla). {e.GetType().Name}: {e.Message}");
                try
                {
                    Destroy(go);
                }
                catch (System.Exception ex) { Swallow.Log(ex); }
            }

            if (coreLoaded)
            {
                var harmony = new Harmony(Guid);
                Patches.GamePatches.ApplyAll(harmony);
            }

            // Only claim the window works if the component that draws it actually loaded -
            // the LogError above is the whole story otherwise.
            if (coreLoaded)
                Log.LogInfo($"{Name} {Version} loaded. Press {UiToggleKey.Value} in-game to open the co-op window.");
        }

        /// <summary>
        /// THE ONLY METHOD IN THIS FILE THAT NAMES CoopCore, and deliberately NoInlining -
        /// the same two-method shape as <see cref="Net.SteamBridge.TryCreate"/> /
        /// <see cref="Net.SteamBridge.Create"/>, for the same reason. A method body that
        /// merely MENTIONS a type that cannot load throws at JIT, before the body (and any
        /// try/catch inside it) ever runs, so the handler that saves us has to live in a
        /// CALLER whose own body is clean. Inline this into Awake and the AddComponent
        /// token - and CoopCore's type-load fault with it - migrates up past the catch,
        /// which takes the whole plugin down on a build where CoopCore can't load.
        ///
        /// The AddComponent lives HERE, not in Awake, on purpose: it is a type-init trigger,
        /// so putting it in Awake's body would force CoopCore's load outside the catch and
        /// defeat the whole arrangement.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AttachCore(GameObject go)
        {
            go.AddComponent<CoopCore>();
        }
    }
}
