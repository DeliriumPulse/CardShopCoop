using System;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using CardShopCoop.Util;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Optional host-authoritative synchronization for RTCGO Custom TV streams.
    /// Local files, mute, and volume deliberately remain local; only stream playback controls
    /// and best-effort stream position cross the wire.</summary>
    public sealed class TvSync : TickableCoopModule
    {
        private const byte Next = 1, Previous = 2, Pause = 3, Power = 4, Shuffle = 5, Seek = 6, Open = 7, Ready = 8;
        private const float Interval = 1f;
        private const float HealEvery = 15f;
        private const float BarrierTimeout = 10f;
        private static TvSync Active;

        public Action<INetMessage> SendOp;
        public Action<INetMessage> BroadcastState;
        public Func<int> PeerCount;
        private float _timer;
        private float _heal;
        private string _lastUrl;
        private string _lastSource;
        private string _lastTitle;
        private string _lastPlaylist;
        private bool _lastLive, _lastSegmented, _lastPaused, _lastPowered, _lastShuffle;
        private int _lastPlaylistIndex;
        private bool _barrier, _hostReady, _resumePulse;
        private float _barrierAge;
        private int _generation;
        private readonly System.Collections.Generic.HashSet<int> _readyPeers = new System.Collections.Generic.HashSet<int>();
        private int _clientGeneration = -1;
        private bool _clientBarrier, _clientReported;

        public TvSync()
        {
            Active = this;
        }

        public override string Name => "tv";

        public override void Start()
        {
            Active = this;
        }

        protected override void OnHostTick(in SyncFrame frame)
        {
            HostTick(frame.Dt, frame.InGame);
        }

        protected override void OnClientTick(in SyncFrame frame)
        {
            ClientTick(frame.Dt, frame.InGame);
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(Active, this))
                Active = null;
        }

        public override void Reset()
        {
            TvInterop.ResetSession();
            _timer = -2.1f;
            _heal = HealEvery;
            _lastUrl = _lastTitle = _lastPlaylist = null;
            _lastSource = null;
            _lastLive = _lastSegmented = _lastPaused = _lastPowered = _lastShuffle = false;
            _lastPlaylistIndex = 0;
            _barrier = _hostReady = _resumePulse = false;
            _barrierAge = 0f;
            _generation = 0;
            _readyPeers.Clear();
            _clientGeneration = -1;
            _clientBarrier = _clientReported = false;
        }

        public override void ForceResend()
        {
            _heal = HealEvery;
        }

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame || !TvInterop.Present || BroadcastState == null)
                return;
            _timer += dt;
            _heal += dt;
            if (_barrier)
                _barrierAge += dt;
            if (_timer < Interval)
                return;
            _timer -= Interval;

            try
            {
                bool streaming = TvInterop.IsPlayingStream || TvInterop.IsFetching;
                string url = streaming ? TvInterop.StreamUrl : null;
                string source = streaming ? TvInterop.SourceUrl : null;
                string title = streaming ? TvInterop.StreamTitle : null;
                string playlist = streaming ? TvInterop.PlaylistUrl : null;
                bool changed = source != _lastSource || url != _lastUrl || title != _lastTitle || playlist != _lastPlaylist
                    || TvInterop.IsLive != _lastLive || TvInterop.IsSegmentedVod != _lastSegmented
                    || TvInterop.Paused != _lastPaused || TvInterop.PoweredOff != _lastPowered
                    || TvInterop.Shuffle != _lastShuffle || TvInterop.PlaylistIndex != _lastPlaylistIndex;
                if (source != _lastSource && !string.IsNullOrEmpty(source))
                {
                    _generation++;
                    _barrier = true;
                    _hostReady = false;
                    _barrierAge = 0f;
                    _readyPeers.Clear();
                    TvInterop.SetSharedPaused(true);
                    changed = true;
                    CoopPlugin.Log.LogInfo("TV load barrier started (generation " + _generation + ")");
                }
                int peers = PeerCount == null ? 0 : Math.Max(0, PeerCount());
                if (_barrier && ((_hostReady && _readyPeers.Count >= peers) || _barrierAge >= BarrierTimeout))
                {
                    if (_barrierAge >= BarrierTimeout && _readyPeers.Count < peers)
                        CoopPlugin.Log.LogWarning("TV load barrier timed out; resuming with " + _readyPeers.Count + "/" + peers + " clients ready");
                    _barrier = false;
                    _resumePulse = true;
                    TvInterop.ResumePlayback();
                    changed = true;
                    CoopPlugin.Log.LogInfo("TV load barrier completed (generation " + _generation + ")");
                }
                if (_barrier && string.IsNullOrEmpty(source))
                {
                    _barrier = false;
                    _hostReady = false;
                    _readyPeers.Clear();
                    _resumePulse = false;
                    TvInterop.ResumePlayback();
                    changed = true;
                    CoopPlugin.Log.LogInfo("TV load barrier cancelled because the host returned to local playback");
                }
                if (!changed && _heal < HealEvery && TvInterop.IsLive)
                    return;

                _lastSource = source;
                _lastUrl = url;
                _lastTitle = title;
                _lastPlaylist = playlist;
                _lastLive = TvInterop.IsLive;
                _lastSegmented = TvInterop.IsSegmentedVod;
                _lastPaused = TvInterop.Paused;
                _lastPowered = TvInterop.PoweredOff;
                _lastShuffle = TvInterop.Shuffle;
                _lastPlaylistIndex = TvInterop.PlaylistIndex;
                _heal = 0f;
                BroadcastState(new TvStateMessage
                {
                    SourceUrl = source,
                    StreamUrl = url,
                    StreamTitle = title,
                    PlaylistUrl = playlist,
                    IsLive = TvInterop.IsLive,
                    IsSegmentedVod = TvInterop.IsSegmentedVod,
                    IsPlaylist = !string.IsNullOrEmpty(playlist),
                    PlaylistIndex = TvInterop.PlaylistIndex,
                    Position = TvInterop.Position,
                    Paused = TvInterop.Paused,
                    PoweredOff = TvInterop.PoweredOff,
                    Shuffle = TvInterop.Shuffle,
                    Barrier = _barrier,
                    Resume = _resumePulse,
                    Generation = _generation
                });
                _resumePulse = false;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TvSync host: " + e.Message); }
        }

        public void HostApplyOp(TvOpMessage message, int senderId)
        {
            if (!TvInterop.Present || message == null)
                return;
            if (message.Op == Ready)
            {
                if (_barrier && (int)message.Value == _generation)
                    _readyPeers.Add(senderId);
                return;
            }
            TvInterop.HostApply(message.Op, message.Url, message.Title, message.Value);
        }

        public void ClientApplyState(TvStateMessage message)
        {
            if (!TvInterop.Present || message == null)
                return;
            if (message.Generation != _clientGeneration)
            {
                _clientGeneration = message.Generation;
                _clientReported = false;
            }
            _clientBarrier = message.Barrier;
            TvInterop.ApplyHostState(message.SourceUrl, message.StreamUrl, message.StreamTitle, message.PlaylistUrl,
                message.IsLive, message.IsSegmentedVod, message.PlaylistIndex, message.Position,
                message.Paused, message.PoweredOff, message.Shuffle,
                message.Barrier, message.Resume, message.Generation);
        }

        public void ClientTick(float dt, bool inGame)
        {
            if (!inGame || !TvInterop.Present)
                return;
            TvInterop.EnsureClientEnrollment();
        }

        public static void LocalPreparedPrefix(object __instance, object source)
        {
            Active?.BeforeLocalPrepared(__instance, source);
        }

        public static void LocalPreparedPostfix(object __instance, object source)
        {
            Active?.OnLocalPrepared(__instance, source);
        }

        private void BeforeLocalPrepared(object instance, object source)
        {
            bool active = CoopCore.Role == CoopRole.Host ? _barrier : _clientBarrier;
            if (active && TvInterop.IsMainPreparedPlayer(instance, source))
                TvInterop.SetSharedPausedForPrepare(false);
        }

        private void OnLocalPrepared(object instance, object source)
        {
            bool active = CoopCore.Role == CoopRole.Host ? _barrier : _clientBarrier;
            if (!active || !TvInterop.IsMainPreparedPlayer(instance, source))
                return;
            TvInterop.PausePreparedStream(instance);
            if (CoopCore.Role == CoopRole.Host)
            {
                _hostReady = true;
            }
            else if (CoopCore.Role == CoopRole.Client && !_clientReported)
            {
                _clientReported = true;
                Send(Ready, value: _clientGeneration);
            }
        }

        private static void Send(byte op, string url = null, string title = null, double value = 0)
        {
            if (CoopCore.Role != CoopRole.Client || TvInterop.ApplyingRemote)
                return;
            CoopCore.Instance?.SendTvOp(new TvOpMessage { Op = op, Url = url, Title = title, Value = value });
        }

        public static bool ClientGlobalInputPrefix()
        {
            if (CoopCore.Role != CoopRole.Client || !TvInterop.Present)
                return true;
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (!alt)
                return true; // retain the mod's own UI opening path (Alt+Y)
            if (Input.GetKeyDown(KeyCode.Y))
                return true;
            if (Input.GetKeyDown(KeyCode.P))
                Send(Pause);
            else if (Input.GetKeyDown(KeyCode.Equals))
                Send(Next);
            else if (Input.GetKeyDown(KeyCode.Minus))
                Send(Previous);
            else if (Input.GetKey(KeyCode.RightBracket))
                Send(Seek, value: 5);
            else if (Input.GetKey(KeyCode.LeftBracket))
                Send(Seek, value: -5);
            else if (Input.GetKeyDown(KeyCode.S))
                Send(Shuffle);
            else if (Input.GetKeyDown(KeyCode.O))
                Send(Power);
            else
                return false; // prevent the TV mod from changing shared state locally
            return false;
        }

        public static bool ClientQualityChoicePrefix(bool download)
        {
            if (CoopCore.Role != CoopRole.Client || !TvInterop.Present)
                return true;
            if (TvInterop.ApplyingRemote)
                return true;
            string url = TvInterop.PendingUrl;
            if (string.IsNullOrWhiteSpace(url))
                return true;
            TvInterop.FinishQualitySelection();
            Send(Open, url, "Shared stream");
            return false;
        }

        public static void ApplyPatches(Harmony h)
        {
            if (!TvInterop.Present)
                return;
            Try(h, "HandleGlobalInput", new HarmonyMethod(typeof(TvSync), nameof(ClientGlobalInputPrefix)));
            Try(h, "OnQualityChoice", new HarmonyMethod(typeof(TvSync), nameof(ClientQualityChoicePrefix)));
            Try(h, "StreamYouTube", null,
                postfix: new HarmonyMethod(typeof(TvInterop), nameof(TvInterop.StreamStartedPostfix)));
            Try(h, "OnLocalPrepared", new HarmonyMethod(typeof(TvSync), nameof(LocalPreparedPrefix)),
                postfix: new HarmonyMethod(typeof(TvSync), nameof(LocalPreparedPostfix)));
            Try(h, "RemotePlayPause", new HarmonyMethod(typeof(TvSync), nameof(ClientPausePrefix)));
            Try(h, "RemoteNext", new HarmonyMethod(typeof(TvSync), nameof(ClientNextPrefix)));
            Try(h, "RemotePrev", new HarmonyMethod(typeof(TvSync), nameof(ClientPreviousPrefix)));
            Try(h, "RemoteShuffle", new HarmonyMethod(typeof(TvSync), nameof(ClientShufflePrefix)));
            Try(h, "RemotePower", new HarmonyMethod(typeof(TvSync), nameof(ClientPowerPrefix)));
            // RemoteMute/RemoteVolUp/RemoteVolDown intentionally remain unpatched: audio is
            // explicitly client-local and must be usable independently on each PC.
        }

        private static bool ClientControl(byte op)
        {
            if (CoopCore.Role != CoopRole.Client || !TvInterop.Present)
                return true;
            Send(op);
            return false;
        }

        public static bool ClientPausePrefix() => ClientControl(Pause);
        public static bool ClientNextPrefix() => ClientControl(Next);
        public static bool ClientPreviousPrefix() => ClientControl(Previous);
        public static bool ClientShufflePrefix() => ClientControl(Shuffle);
        public static bool ClientPowerPrefix() => ClientControl(Power);

        private static void Try(Harmony h, string method, HarmonyMethod prefix, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(AccessTools.TypeByName("Scripts.VideoPlayerController"), method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("TV patch target missing: " + method);
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TV patch failed " + method + ": " + e.Message); }
        }

    }
}
