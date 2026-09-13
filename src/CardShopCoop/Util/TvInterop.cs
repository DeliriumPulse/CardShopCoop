using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>Optional reflection bridge to RTCGO Custom TV. This assembly is loaded by
    /// Enhanced Prefab Loader rather than BepInEx, so the bridge must remain harmless when the
    /// TV pack is absent or its private implementation changes.</summary>
    public static class TvInterop
    {
        private const string AssemblyName = "RTCGOCustomScripts";
        private static readonly Type T = ReflectionSurface.OptionalType("Scripts.VideoPlayerController", AssemblyName);
        private const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo FiEnrolled = Field("enrolledTVs");
        private static readonly FieldInfo FiSharedVideoIndex = Field("sharedVideoIndex");
        private static readonly FieldInfo FiStreamUrl = Field("activeStreamUrl");
        private static readonly FieldInfo FiStreamTitle = Field("activeStreamTitle");
        private static readonly FieldInfo FiPlaylistUrl = Field("activePlaylistUrl");
        private static readonly FieldInfo FiPlaylistIndex = Field("activePlaylistIndex");
        private static readonly FieldInfo FiLive = Field("isLiveStream");
        private static readonly FieldInfo FiSegmented = Field("isSegmentedVod");
        private static readonly FieldInfo FiPlaying = Field("isPlayingStream");
        private static readonly FieldInfo FiFetching = Field("isFetchingUrl");
        private static readonly FieldInfo FiPaused = Field("isSharedPaused");
        private static readonly FieldInfo FiPowered = Field("isPoweredOff");
        private static readonly FieldInfo FiShuffle = Field("isShuffleMode");
        private static readonly FieldInfo FiLastTime = Field("lastKnownMasterTime");
        private static readonly FieldInfo FiPendingUrl = Field("pendingYouTubeUrl");
        private static readonly FieldInfo FiQualityOpen = Field("isQualityChoiceOpen");
        private static readonly FieldInfo FiQualityRoot = Field("qualityChoiceRoot");
        private static readonly MethodInfo MiChangeUrl = Method("ChangeToUrl", typeof(string), typeof(string));
        private static readonly MethodInfo MiPlaylist = Method("PlayPlaylistItem", typeof(int));
        private static readonly MethodInfo MiStream = Method("StreamYouTube", typeof(string));
        private static readonly MethodInfo MiQualityChoice = Method("OnQualityChoice", typeof(bool));
        private static readonly MethodInfo MiChangeVideo = Method("ChangeVideo", typeof(int));
        private static readonly MethodInfo MiBroadcastChange = Method("BroadcastChange", typeof(int));
        private static readonly MethodInfo MiTogglePause = Method("ToggleGlobalPause");
        private static readonly MethodInfo MiRemotePower = Method("RemotePower");
        private static readonly MethodInfo MiUnfreeze = Method("UnfreezeGame");
        private static bool _logged;
        private static string _sourceUrl;
        private static string _launchedSource;
        private static int _launchedPlaylistIndex;
        private static bool _enrollmentRequested;

        public static bool Present
        {
            get
            {
                bool ok = T != null && FiEnrolled != null && MiChangeUrl != null && MiStream != null;
                if (ok && !_logged)
                {
                    _logged = true;
                    CoopPlugin.Log.LogInfo("RTCGO Custom TV detected - stream state will be synchronized when co-op is active");
                }
                return ok;
            }
        }

        public static bool ApplyingRemote;

        private static FieldInfo Field(string name) => ReflectionSurface.OptionalField(T, name);
        private static MethodInfo Method(string name, params Type[] args) => ReflectionSurface.OptionalMethod(T, name, args);

        private static object Get(FieldInfo field)
        {
            return field == null ? null : field.GetValue(field.IsStatic ? null : Master());
        }

        private static TVal Get<TVal>(FieldInfo field, TVal fallback = default(TVal))
        {
            try
            {
                object v = Get(field);
                return v == null ? fallback : (TVal)v;
            }
            catch (System.Exception e) { Swallow.Log(e); return fallback; }
        }

        private static Component Master()
        {
            try
            {
                var list = FiEnrolled?.GetValue(null) as IList;
                if (list != null && list.Count > 0)
                    return list[0] as Component;
                return UnityEngine.Object.FindObjectOfType(T) as Component;
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        public static string StreamUrl => Get<string>(FiStreamUrl);
        public static string StreamTitle => Get<string>(FiStreamTitle);
        public static string PlaylistUrl => Get<string>(FiPlaylistUrl);
        public static bool IsPlayingStream => Get(FiPlaying, false);
        public static bool IsFetching => Get(FiFetching, false);
        public static bool IsLive => Get(FiLive, false);
        public static bool IsSegmentedVod => Get(FiSegmented, false);
        public static bool Paused => Get(FiPaused, false);
        public static bool PoweredOff => Get(FiPowered, false);
        public static bool Shuffle => Get(FiShuffle, false);
        public static int PlaylistIndex => Get(FiPlaylistIndex, 1);
        public static double Position => Get(FiLastTime, 0.0);

        public static bool IsPlaylist => !string.IsNullOrEmpty(PlaylistUrl);
        public static string SourceUrl => _sourceUrl;
        public static int EnrolledCount
        {
            get
            {
                return (FiEnrolled?.GetValue(null) as IList)?.Count ?? 0;
            }
        }

        public static void RecordSourceUrl(string url)
        {
            if (CoopCore.Role == CoopRole.Host && !string.IsNullOrWhiteSpace(url))
                _sourceUrl = url;
        }

        public static void ResetSession()
        {
            _sourceUrl = null;
            _launchedSource = null;
            _launchedPlaylistIndex = 0;
            _enrollmentRequested = false;
        }

        public static bool ApplyState(string sourceUrl, string url, string title, string playlistUrl, bool live,
            bool segmented, int playlistIndex, double position, bool paused, bool powered, bool shuffle,
            bool barrier, bool resume, int generation)
        {
            if (!Present)
                return false;
            var master = Master();
            if (master == null)
                return false;
            ApplyingRemote = true;
            try
            {
                string oldSource = _launchedSource;
                FiLive?.SetValue(null, live);
                FiSegmented?.SetValue(null, segmented);
                FiStreamTitle?.SetValue(null, title);
                FiPlaylistUrl?.SetValue(null, playlistUrl);
                FiPlaylistIndex?.SetValue(null, playlistIndex);
                FiPowered?.SetValue(null, powered);
                FiShuffle?.SetValue(null, shuffle);

                bool newPlaylistItem = !string.IsNullOrEmpty(playlistUrl)
                    && string.Equals(oldSource, sourceUrl, StringComparison.Ordinal)
                    && _launchedPlaylistIndex != playlistIndex;
                if (!string.IsNullOrEmpty(sourceUrl)
                    && (!string.Equals(oldSource, sourceUrl, StringComparison.Ordinal) || newPlaylistItem))
                {
                    // The host's activeStreamUrl is a short-lived yt-dlp result and cannot
                    // reliably be consumed by another machine. Resolve the original page URL
                    // locally so each player gets its own valid direct media URL.
                    _launchedSource = sourceUrl;
                    _launchedPlaylistIndex = playlistIndex;
                    if (EnrolledCount > 0)
                    {
                        CoopPlugin.Log.LogInfo("TV client injecting shared stream through RTCGO quality selection: " + sourceUrl);
                        FiPendingUrl?.SetValue(null, sourceUrl);
                        ApplyingRemote = true;
                        try
                        {
                            MiQualityChoice?.Invoke(master, new object[] { false });
                        }
                        finally { ApplyingRemote = false; }
                    }
                    else
                    {
                        _launchedSource = null;
                        _launchedPlaylistIndex = 0;
                    }
                }
                else if (string.IsNullOrEmpty(sourceUrl) && string.IsNullOrEmpty(playlistUrl))
                {
                    // The host returned to local playback. Allow a later selection of the
                    // same URL to launch again instead of being suppressed by the old memo.
                    bool wasStreaming = !string.IsNullOrEmpty(_launchedSource) || _launchedPlaylistIndex != 0;
                    _launchedSource = null;
                    _launchedPlaylistIndex = 0;
                    if (wasStreaming)
                    {
                        MiBroadcastChange?.Invoke(master, new object[] { Get(FiSharedVideoIndex, 0) });
                    }
                }

                SetSharedPaused(barrier || paused || powered);
                if (resume)
                    ResumePlayback();
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("TV apply: " + e.Message);
                return false;
            }
            finally { ApplyingRemote = false; }
        }

        public static bool ApplyHostState(string sourceUrl, string url, string title, string playlistUrl, bool live,
            bool segmented, int playlistIndex, double position, bool paused, bool powered, bool shuffle,
            bool barrier, bool resume, int generation)
        {
            return ApplyState(sourceUrl, url, title, playlistUrl, live, segmented, playlistIndex,
                position, paused, powered, shuffle, barrier, resume, generation);
        }

        public static void SetSharedPaused(bool paused)
        {
            bool changed = Paused != paused;
            if (changed && MiTogglePause != null)
                MiTogglePause.Invoke(Master(), null);
            if (Paused != paused)
                FiPaused?.SetValue(null, paused);
        }

        public static void SetSharedPausedForPrepare(bool paused)
        {
            FiPaused?.SetValue(null, paused);
        }

        public static void ResumePlayback()
        {
            ResumePlaybackThroughMod();
        }

        public static bool IsStreamCandidate(object instance)
        {
            if (instance == null || !IsPlayingStream)
                return false;
            var master = Master();
            return ReferenceEquals(master, instance);
        }

        public static bool IsMainPreparedPlayer(object instance, object source)
        {
            return IsStreamCandidate(instance) && ReferenceEquals(VideoPlayer(instance as Component), source);
        }

        public static void PausePreparedStream(object instance)
        {
            if (!IsStreamCandidate(instance))
                return;
            SetSharedPaused(true);
        }

        public static void ResumePlaybackThroughMod()
        {
            SetSharedPaused(false);
        }

        public static void EnsureClientEnrollment()
        {
            if (CoopCore.Role != CoopRole.Client || !Present || EnrolledCount > 0
                || IsPlayingStream || IsFetching || _enrollmentRequested)
                return;
            var master = Master();
            if (master == null || MiChangeVideo == null)
                return;
            _enrollmentRequested = true;
            int index = Get(FiSharedVideoIndex, 0);
            CoopPlugin.Log.LogInfo("TV client initializing local playback for RTCGO enrollment");
            MiChangeVideo.Invoke(master, new object[] { index });
        }

        public static void StreamStartedPostfix(string url)
        {
            RecordSourceUrl(url);
        }

        public static void HostApply(byte op, string url, string title, double value)
        {
            if (!Present)
                return;
            var master = Master();
            if (master == null)
                return;
            try
            {
                switch (op)
                {
                    case 1: // next playlist item
                        if (IsPlaylist)
                            MiPlaylist?.Invoke(master, new object[] { PlaylistIndex + 1 });
                        break;
                    case 2: // previous playlist item
                        if (IsPlaylist)
                            MiPlaylist?.Invoke(master, new object[] { Math.Max(1, PlaylistIndex - 1) });
                        break;
                    case 3:
                        MiTogglePause?.Invoke(master, null);
                        break;
                    case 4:
                        if (MiRemotePower != null)
                            MiRemotePower.Invoke(master, null);
                        else
                            FiPowered?.SetValue(null, !PoweredOff);
                        break;
                    case 5:
                        FiShuffle?.SetValue(null, !Shuffle);
                        break;
                    case 6:
                        var vp = VideoPlayer(master);
                        if (vp != null && GetProperty<bool>(vp, "isPrepared"))
                            SetProperty(vp, "time", Math.Max(0.0, GetProperty<double>(vp, "time") + value));
                        FiLastTime?.SetValue(null, vp == null ? Position : GetProperty<double>(vp, "time"));
                        break;
                    case 7: // stream URL supplied by a joiner; always stream, never download
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            FiPlaylistUrl?.SetValue(null, url.Contains("list=") ? url : null);
                            if (MiStream != null)
                                ((MonoBehaviour)master).StartCoroutine((IEnumerator)MiStream.Invoke(master, new object[] { url }));
                            else
                                MiChangeUrl?.Invoke(master, new object[] { url, title ?? "Shared stream" });
                        }
                        break;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TV host op " + op + ": " + e.Message); }
        }

        public static bool FinishQualitySelection()
        {
            if (!Present)
                return false;
            try
            {
                string url = Get<string>(FiPendingUrl);
                if (string.IsNullOrWhiteSpace(url))
                    return false;
                MiUnfreeze?.Invoke(Master(), null);
                FiQualityOpen?.SetValue(null, false);
                (FiQualityRoot?.GetValue(null) as GameObject)?.SetActive(false);
                return true;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TV quality selection: " + e.Message); return false; }
        }

        public static string PendingUrl => Get<string>(FiPendingUrl);

        private static object VideoPlayer(Component master)
        {
            return master == null ? null : ReflectionSurface.OptionalField(master.GetType(), "m_VideoPlayer")?.GetValue(master);
        }

        private static TVal GetProperty<TVal>(object target, string name)
        {
            try
            {
                return (TVal)ReflectionSurface.OptionalProperty(target.GetType(), name).GetValue(target, null);
            }
            catch (System.Exception e) { Swallow.Log(e); return default(TVal); }
        }

        private static void SetProperty(object target, string name, object value)
        {
            target?.GetType().GetProperty(name, F)?.SetValue(target, value, null);
        }

        private static void Invoke(object target, string name)
        {
            if (target == null)
                return;
            ReflectionSurface.OptionalMethod(target.GetType(), name)?.Invoke(target, null);
        }
    }
}
