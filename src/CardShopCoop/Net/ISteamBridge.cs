using CardShopCoop.Util;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CardShopCoop.Net
{
    /// <summary>
    /// THE STEAM ISOLATION BOUNDARY.
    ///
    /// The Game Pass build of Card Shop Simulator ships NO
    /// com.rlabrecque.steamworks.net.dll. The CLR's failure mode for that is brutal and
    /// non-obvious: a value-type (struct) FIELD whose type lives in a missing assembly
    /// kills the load of the whole CONTAINING TYPE, and a method body that merely
    /// mentions such a type (including generic instantiations like Action&lt;CSteamID&gt;
    /// or List&lt;SteamLobby.LobbyRow&gt;) throws at JIT the first time it is called.
    /// A try/catch in the SAME method never catches that - the throw happens before the
    /// body runs, so the catch must live in a CALLER whose own body is clean.
    ///
    /// So: nothing in this file may EVER name a Steamworks type. No `using Steamworks;`,
    /// no CSteamID, not even in a generic argument. Deleting that using statement from
    /// CoopCore.cs and CoopUI.cs is what turns "did I miss a reference?" into a compiler
    /// error, and this file is what lets those two compile without it.
    ///
    /// Everything Steam-typed lives behind <see cref="ISteamBridge"/>, whose only
    /// implementation (SteamBridgeImpl, in SteamNet.cs) is allowed to fail to load. The
    /// single path that reaches it is <see cref="SteamBridge.TryCreate"/>.
    /// </summary>
    /// <remarks>
    /// Steam-free mirror of SteamLobby.LobbyRow. Identical shape except that the lobby
    /// id is the raw ulong instead of a CSteamID - which is precisely the point: CoopUI
    /// keeps a List&lt;LobbyRow&gt; local, and a List of a struct that cannot load takes
    /// the whole method with it. SteamBridgeImpl converts.
    /// </remarks>
    public struct LobbyRow
    {
        public ulong Id;
        public string Name;
        public int Players;
        public int Max;
        public bool HasPw;
        public string Ver;
    }

    /// <summary>
    /// Everything CoopCore and CoopUI are allowed to know about Steam. Every member is
    /// Steam-free BY CONSTRUCTION - ulong / string / bool / int / Action / Action&lt;ulong&gt;
    /// / List&lt;LobbyRow&gt; / ICoopTransport - so the always-loaded types may reference
    /// this interface as freely as they like. The CLR resolves the implementing type only
    /// at runtime, and only on a build where it can actually load.
    ///
    /// A null ISteamBridge means "the Steamworks ASSEMBLY is absent" (Game Pass / DRM-free)
    /// and Steam can never work here - hide the UI, never show an error. That is a
    /// different thing from <see cref="SteamAvailable"/> returning false, which means
    /// "Steam is installed but the client isn't running" - a state the player can fix, and
    /// which keeps today's error message. Never conflate the two.
    /// </summary>
    public interface ISteamBridge
    {
        /// <summary>Create the long-lived lobby callbacks. Call once, at CoopCore.Awake:
        /// the invite-accepted callback must be listening from startup so accepting an
        /// invite at any moment (or +connect_lobby) starts the join flow.</summary>
        void Init();

        /// <summary>Is the Steam CLIENT running? (The assembly being present is a
        /// separate question, already answered by this object being non-null.)</summary>
        bool SteamAvailable();

        /// <summary>Local Steam persona, or an empty string when unavailable.</summary>
        string LocalPersonaName
        {
            get;
        }

        /// <summary>Local Steam identity, or zero when Steam is unavailable.</summary>
        ulong LocalSteamId
        {
            get;
        }

        /// <summary>Local nickname for a Steam friend, or an empty string when the
        /// friend is not known locally or Steam is unavailable.</summary>
        string FriendNickname(ulong steamId);

        /// <summary>Build the Steam P2P transport. MUST be called BEFORE Host()/Join():
        /// the lobby callbacks inside the bridge write the lobby id (and, on the client,
        /// the host connection) into the transport this returns. Reorder these two and
        /// the callback fires against a null transport and the Steam path silently never
        /// connects - on the Steam build, where nobody is looking for it.</summary>
        ICoopTransport CreateTransport(bool isHost, INetMessage keepalive);

        void Host(bool isPublic, string lobbyName, bool hasPassword);
        void Join(ulong lobbyId);
        void Leave();
        void OpenInviteDialog();
        void RefreshList();
        bool ListRefreshing
        {
            get;
        }

        /// <summary>Public-lobby browser rows, already converted to the Steam-free
        /// <see cref="LobbyRow"/>. The list is reused between calls (OnGUI polls it every
        /// frame); treat it as read-only and never hold it across frames.
        ///
        /// THE GETTER REBUILDS THAT ONE SHARED LIST ON EVERY READ - it is not a property
        /// access, it is a clear-and-refill of the same instance. So: read it ONCE per
        /// frame into a local and work off that, and NEVER enumerate it re-entrantly (a
        /// second read taken while a foreach over the first is still running clears the
        /// list out from under that foreach - InvalidOperationException, from a getter
        /// that looks like a plain field).</summary>
        List<LobbyRow> Lobbies
        {
            get;
        }

        Action<string> OnError
        {
            get; set;
        }

        /// <summary>Host: the lobby exists and the transport is already wired to it;
        /// arg = the created lobby id. Steam-free by construction, like every other member
        /// here - the id is the raw ulong, never a CSteamID.</summary>
        Action<ulong> OnLobbyLive
        {
            get; set;
        }

        /// <summary>Client: we entered a lobby and the transport is already wired to its
        /// owner. The ROLE CHECK IS THE SUBSCRIBER'S JOB - see CoopCore.</summary>
        Action OnConnectedToHost
        {
            get; set;
        }

        /// <summary>The local player accepted somebody's invite; arg = lobby id.</summary>
        Action<ulong> OnInviteAccepted
        {
            get; set;
        }
    }

    /// <summary>
    /// Reflection-only presence test for com.rlabrecque.steamworks.net. Cached once, never
    /// throws, and never names a Steamworks type in metadata - the assembly and the type
    /// are STRINGS, which is the whole trick.
    /// </summary>
    public static class PlatformProbe
    {
        private static int _state; // 0 = unknown, 1 = present, 2 = absent

        /// <summary>True when the Steamworks.NET assembly can be resolved in this process.
        /// Evaluated at most once per session.</summary>
        public static bool SteamworksPresent
        {
            get
            {
                if (_state == 0)
                    _state = Detect() ? 1 : 2;
                return _state == 1;
            }
        }

        private static bool Detect()
        {
            // (a) Already loaded? Cheapest, and true on the Steam build once the game's
            //     Heathen integration has touched Steamworks. Matched by SIMPLE NAME, so
            //     it is immune to strong-name and version differences.
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (string.Equals(a.GetName().Name, "com.rlabrecque.steamworks.net",
                                      StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch (System.Exception e) { Swallow.Log(e); }

            // (b) Not loaded YET? Force a partial bind. DO NOT "SIMPLIFY" THIS AWAY: our
            //     Awake can easily run before the game first touches Steamworks, so (a)
            //     alone would return a false negative on a real Steam install and hide the
            //     Steam UI permanently. This is the branch that actually fires on Steam.
            //     throwOnError:false - it must never throw on Game Pass.
            try
            {
                if (Type.GetType("Steamworks.SteamAPI, com.rlabrecque.steamworks.net", false) != null)
                    return true;
            }
            catch (System.Exception e) { Swallow.Log(e); }

            return false;
        }

        // =====================================================================
        // SAVE-BACKEND ORACLE (added 2026-08-17 for 1.0.39).
        //
        // NEVER USE SteamworksPresent TO DECIDE ANYTHING ABOUT SAVES. It answers a
        // question about OUR dll-resolution environment ("can this process bind the
        // Steamworks wrapper?"), not about which backend the GAME writes saves with, and
        // the field proved the difference: a Game Pass install carrying a stray
        // com.rlabrecque.steamworks.net.dll (bundled by another mod, or left over from a
        // fork) answers TRUE, so both of SaveTransfer's gates took the Steam branch -
        // hosting threw FileNotFoundException demanding a savedGames_Release6.json the
        // wgs backend never writes, and joining skipped the m_SavedGame injection and
        // loaded the guest's own world instead of the host's. Assembly presence is the
        // right oracle for "can Steam UI exist here", the wrong one for "where do saves
        // live". Two different questions; keep them apart.
        //
        // The oracle that IS right is the game's own save-completion counter -
        // CPlayerData.m_SaveIndex/m_SaveCycle, bumped inside CGameData.SaveGameData
        // UPSTREAM of the backend call, so it advances identically on the Steam json
        // writer and on the Game Pass Gamecore/wgs writer. Sample it either side of
        // CGameManager.SaveGameData: a bump is positive proof the save ran all the way
        // through its four guards, no bump is positive proof it bailed. That is the same
        // completion proof the slot-file freshness clock buys on Steam, obtained from the
        // game instead of from the filesystem, and it needs no platform knowledge at all.
        //
        // <see cref="GamePassBuild"/> below is the LAST-RESORT tiebreak, used only when
        // the counter fields cannot be found (an unknown future build) - never as the
        // primary answer.
        //
        // Everything here is string-keyed reflection: this file names no game type in its
        // metadata either, so nothing in it can fault at JIT on a reshaped build.
        // =====================================================================

        private static Assembly _gameAsm;
        private static bool _gameAsmSearched;

        /// <summary>The game's Assembly-CSharp, by SIMPLE NAME, or null. Cached.</summary>
        private static Assembly GameAssembly()
        {
            if (!_gameAsmSearched)
            {
                _gameAsmSearched = true;
                try
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                        if (string.Equals(a.GetName().Name, "Assembly-CSharp",
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            _gameAsm = a;
                            break;
                        }
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            return _gameAsm;
        }

        private static FieldInfo _saveIndexField, _saveCycleField;
        private static bool _counterSearched;

        /// <summary>
        /// PRIMARY SAVE ORACLE. Reads the game's save-completion counter pair
        /// (CPlayerData.m_SaveIndex, CPlayerData.m_SaveCycle). Returns false - meaning
        /// "unknown build, this oracle cannot answer" - when either field is missing;
        /// callers must fall through to <see cref="GamePassBuild"/> in that case, never
        /// treat false as "the save did not run".
        ///
        /// Sample it BEFORE and AFTER CGameManager.SaveGameData and compare the PAIR: the
        /// index wraps to 0 and bumps the cycle at 1e9, so index alone is not enough.
        ///
        /// Read CPlayerData's statics, NOT CSaveLoad.m_SavedGame.m_SaveIndex: on Game Pass
        /// the Gamecore writer may not re-point m_SavedGame the way CSaveLoad.Save does,
        /// but CPlayerData's counter is bumped before the backends split.
        /// </summary>
        public static bool TrySampleSaveCounter(out int index, out int cycle)
        {
            index = 0;
            cycle = 0;
            try
            {
                if (!_counterSearched)
                {
                    _counterSearched = true;
                    var t = GameAssembly()?.GetType("CPlayerData");
                    if (t != null)
                    {
                        _saveIndexField = ReflectionSurface.OptionalField(t, "m_SaveIndex");
                        _saveCycleField = ReflectionSurface.OptionalField(t, "m_SaveCycle");
                    }
                }
                if (_saveIndexField == null || _saveCycleField == null)
                    return false;
                index = (int)_saveIndexField.GetValue(null);
                cycle = (int)_saveCycleField.GetValue(null);
                return true;
            }
            catch { index = 0; cycle = 0; return false; }
        }

        private static int _gpState; // 0 = unknown, 1 = Game Pass, 2 = not

        /// <summary>
        /// LAST-RESORT save-backend tiebreak: does the game assembly contain a Gamecore
        /// (Xbox Game Save / wgs) type? Derived from the GAME, not from our dll
        /// environment, which is what makes it legitimate where SteamworksPresent is not.
        /// Verified absent on the Steam build (zero hits for "Gamecore" across the whole
        /// 608-file decompile), so a match means Game Pass. SUBSTRING match on purpose:
        /// the exact Game Pass type name is community hearsay and we have no Game Pass
        /// assembly to check it against. Cached; never throws.
        ///
        /// Only consult this when <see cref="TrySampleSaveCounter"/> returns false.
        /// </summary>
        public static bool GamePassBuild
        {
            get
            {
                if (_gpState == 0)
                    _gpState = DetectGamecore() ? 1 : 2;
                return _gpState == 1;
            }
        }

        private static bool DetectGamecore()
        {
            var asm = GameAssembly();
            if (asm == null)
                return false;
            Type[] types;
            // a partially-loadable assembly still tells us what we need: walk .Types and
            // skip the nulls rather than giving up on the whole probe
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch (System.Exception e) { Swallow.Log(e); return false; }
            if (types == null)
                return false;
            foreach (var t in types)
            {
                if (t == null)
                    continue;
                try
                {
                    if (t.Name.IndexOf("Gamecore", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            return false;
        }

        /// <summary>One-line save-backend verdict for the startup log. Deliberately
        /// separate from the Steamworks line: they answer different questions, and 1.0.38
        /// shipped a field report where the Steam line was true and the save backend was
        /// not.</summary>
        public static string SaveBackendDescription
        {
            get
            {
                int i, c;
                bool counter = TrySampleSaveCounter(out i, out c);
                return (GamePassBuild ? "Xbox containers (Game Pass)" : "local files")
                     + (counter
                        ? " [save-completion counter available]"
                        : " [save-completion counter UNAVAILABLE - unknown build, falling back to the Gamecore type probe]");
            }
        }
    }

    /// <summary>Factory for the one and only ISteamBridge implementation.</summary>
    public static class SteamBridge
    {
        /// <summary>
        /// THE boundary call. Returns null on any build where Steam cannot work, and never
        /// throws.
        ///
        /// DO NOT REMOVE THE [MethodImpl(NoInlining)] ATTRIBUTES ON THIS METHOD OR ON
        /// <see cref="Create"/>. Both are tiny, and Mono's JIT inlines methods this size
        /// aggressively. Inlined, the `newobj SteamBridgeImpl` token migrates up into
        /// CoopCore.Awake, and the type-load fault moves with it - which kills the entire
        /// mod on Game Pass, with no code change that looks even slightly related.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ISteamBridge TryCreate()
        {
            if (!PlatformProbe.SteamworksPresent)
                return null;
            try
            {
                return Create();
            }
            catch (Exception e)
            {
                // Reached when the assembly probed present but the type still failed to
                // load (a wrong/partial Steamworks build). Warn, then degrade to LAN.
                CoopPlugin.Log.LogWarning("Steam bridge unavailable: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Deliberately a separate method, and deliberately NoInlining. This is the ONLY
        /// method in the plugin outside SteamNet.cs that names a Steam-bearing type, so it
        /// is the only one that can fail to JIT. The try/catch that saves us therefore has
        /// to sit in TryCreate, whose own body is Steamworks-free: a try/catch wrapped
        /// around `new SteamBridgeImpl()` INSIDE one method would catch nothing at all,
        /// because the throw happens at JIT time, before the body (and its handlers) ever
        /// run. DO NOT MERGE THESE TWO METHODS.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ISteamBridge Create()
        {
            return new SteamBridgeImpl();
        }
    }
}
