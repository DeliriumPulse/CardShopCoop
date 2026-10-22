using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using CardShopCoop.Net;
using CardShopCoop.Sync;
using CardShopCoop.Util;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        private const int MaxConnectionReportChars = 48000;
        private static readonly Regex SteamIdPattern = new Regex(@"\b\d{17}\b", RegexOptions.Compiled);
        private static readonly Regex IpAddressPattern = new Regex(@"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?(?!\d)", RegexOptions.Compiled);
        private static readonly Regex UserPathPattern = new Regex(@"(?i)[A-Z]:\\Users\\[^\\\s]+", RegexOptions.Compiled);
        private static readonly Regex SecretPattern = new Regex(@"(?i)(password|passcode|token|secret|authorization|credential)(\s*[:=]\s*)[^\s,;]+", RegexOptions.Compiled);
        private readonly RecoveryStatus _recovery = new RecoveryStatus();
        private readonly DiagnosticHistory _interactionHistory = new DiagnosticHistory();
        private string _interactionLine = "";
        private double _interactionUntil;
        private double _nextInteractionNotice;
        private string _lastSessionReport;
        private long _receivedSaveBytes;
        private long _receivedBundleBytes;
        private string _connectionHealth = "Connection progress has not been sampled yet.";
        private double _nextHealthSample;
        public string ConnectionHealthLine => _net == null ? "No multiplayer connection." : _connectionHealth;
        public string InteractionLine => Time.realtimeSinceStartupAsDouble < _interactionUntil ? _interactionLine : "";
        public string RecoveryLine => _recovery.Message;
        public bool CanRefreshGuestObjects => Role == CoopRole.Client && _net != null
            && _net.ConnectionCount > 0 && !ClientReloading && InGameLevel()
            && _joinWatchdog.Current == JoinWatchdog.Stage.Idle
            && _recovery.CanRequest(Time.realtimeSinceStartupAsDouble);

        internal void ReportInteraction(string message)
        {
            if (Role != CoopRole.Client || ClientReloading)
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            // Frequently polled vanilla predicates must not flood the HUD or log.
            if (now < _nextInteractionNotice)
                return;
            _nextInteractionNotice = now + 3;
            _interactionLine = message;
            _interactionUntil = now + 8;
            _interactionHistory.Add(DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC: " + message);
            CoopPlugin.Log.LogInfo("Guest interaction: " + message);
        }

        public void RefreshGuestObjects()
        {
            if (!CanRefreshGuestObjects || !_recovery.Begin(Time.realtimeSinceStartupAsDouble))
                return;
            // Use the existing coalesced request. Never clear escrow, claim ownership,
            // replay an item mutation, or report success before receiving host state.
            _boxEngine.RequestResyncCoalesced();
            CoopPlugin.Log.LogInfo("Guest requested object refresh from the co-op window");
        }

        private void TickInteractionDiagnostics()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= _nextHealthSample)
            {
                _nextHealthSample = now + 1;
                double queued = 0, stalled = 0;
                var queues = (_net as ITransportQueueDiagnostics)?.GetSendQueueStatus();
                if (queues != null)
                    foreach (var queue in queues)
                    {
                        queued = Math.Max(queued, queue.OldestSeconds);
                        stalled = Math.Max(stalled, queue.StalledSeconds);
                    }
                _connectionHealth = SessionHealth.Describe(_boxEngine.PendingTransferCount + _world.PendingTransferCount + _boxEngine.PendingRepairCount,
                    Math.Max(_boxEngine.OldestPendingRepairSeconds,
                        Math.Max(_boxEngine.OldestPendingTransferSeconds, _world.OldestPendingTransferSeconds)), queued, stalled);
            }
            if (Role != CoopRole.Client || ClientReloading || !InGameLevel())
                return;
            if (_recovery.Tick(Time.realtimeSinceStartupAsDouble))
                ReportInteraction(_recovery.Message);
            double oldest = Math.Max(_boxEngine.OldestPendingRepairSeconds,
                Math.Max(_boxEngine.OldestPendingTransferSeconds, _world.OldestPendingTransferSeconds));
            if (oldest >= 5)
                ReportInteraction("An item update is delayed; its sync request is being retried. Connection help shows its current progress.");
        }

        private string ConnectionSnapshot()
        {
            var text = new StringBuilder();
            text.AppendLine("UTC: " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture));
            text.AppendLine("Mod: " + CoopPlugin.Version + " | wire: " + Msg.WireVersion);
            text.AppendLine("Game: " + Application.version + " | Unity: " + Application.unityVersion);
            text.AppendLine("Role: " + Role + " | transport: " + (_net == null ? "none" : IsSteamSession ? "Steam" : "LAN/TCP"));
            text.AppendLine("Scene: " + SceneManager.GetActiveScene().name + " | loading borrowed world: " + ClientReloading);
            double now = Time.realtimeSinceStartupAsDouble;
            text.AppendLine("Join stage: " + _joinWatchdog.Current + " | stage age: " + _joinWatchdog.StageAgeSeconds(now).ToString("F1", CultureInfo.InvariantCulture)
                + "s | last hello age: " + _joinWatchdog.HelloAgeSeconds(now).ToString("F1", CultureInfo.InvariantCulture) + "s");
            text.AppendLine("Status: " + RedactSensitiveText(StatusLine));
            text.AppendLine("Current error: " + RedactSensitiveText(string.IsNullOrEmpty(ErrorLine) ? "none" : ErrorLine));
            var currency = CaptureCurrency();
            text.AppendLine("Local currency: " + (EMoneyCurrencyType)currency.CurrencyId + " | rate: " + currency.ConversionRate.ToString("R", CultureInfo.InvariantCulture));
            text.AppendLine("Session currency: " + (_sessionCurrency == null ? "not agreed" :
                ((EMoneyCurrencyType)_sessionCurrency.CurrencyId) + " | rate: " + _sessionCurrency.ConversionRate.ToString("R", CultureInfo.InvariantCulture)));
            text.AppendLine("Received save bytes (compressed): " + _receivedSaveBytes + "/" + _saveExpected);
            text.AppendLine("Received bundle bytes (compressed): " + _receivedBundleBytes + "/" + _bundleExpected + " (-1 expected = not advertised)");
            text.AppendLine("Peers: " + (_net?.ConnectionCount ?? 0) + " | queued incoming: " + (_net?.Incoming.Count ?? 0)
                + " | queued connects/disconnects: " + (_net?.Connects.Count ?? 0) + "/" + (_net?.Disconnects.Count ?? 0)
                + " | transport timeout: " + (_net == null ? "n/a" : _net.TimeoutSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s"));
            if (_net != null)
            {
                double oldestSilence = 0;
                foreach (int id in _net.ConnIds())
                    oldestSilence = Math.Max(oldestSilence, _net.SecondsSinceLastRecv(id));
                text.AppendLine("Longest peer receive silence: " + oldestSilence.ToString("F1", CultureInfo.InvariantCulture) + "s (not ping latency)");
            }
            text.AppendLine("Pending box transfers: " + _boxEngine.PendingTransferCount + " | oldest seconds: " + _boxEngine.OldestPendingTransferSeconds.ToString("F1", CultureInfo.InvariantCulture));
            text.AppendLine("Pending shelf transfers: " + _world.PendingTransferCount + " | oldest seconds: " + _world.OldestPendingTransferSeconds.ToString("F1", CultureInfo.InvariantCulture));
            text.AppendLine("Guest item request retries this session: boxes=" + _boxEngine.TransferRetryCount
                + " | shelves=" + _world.TransferRetryCount);
            text.AppendLine("Ignored invalid item replies: boxes=" + _boxEngine.InvalidTransferResultCount
                + " | shelves=" + _world.InvalidTransferResultCount);
            text.AppendLine("Targeted box repairs: waiting=" + _boxEngine.PendingRepairCount
                + " | oldest seconds=" + _boxEngine.OldestPendingRepairSeconds.ToString("F1", CultureInfo.InvariantCulture)
                + " | retries=" + _boxEngine.RepairRetryCount);
            text.AppendLine("Queued shared-wallet debits: events=" + _queuedSpend.Count
                + " | amount=" + _queuedSpend.Total.ToString("F3", CultureInfo.InvariantCulture));
            text.AppendLine("Connection progress: " + ConnectionHealthLine);
            var sendQueues = (_net as ITransportQueueDiagnostics)?.GetSendQueueStatus();
            if (sendQueues == null)
                text.AppendLine("Outgoing queue metrics: unavailable for this transport (not measured as zero)");
            else if (sendQueues.Count == 0)
                text.AppendLine("Outgoing reliable queues: empty");
            else
                foreach (var queue in sendQueues)
                    text.AppendLine("Outgoing reliable peer " + queue.ConnectionId + ": frames=" + queue.Frames
                        + " | bytes=" + queue.Bytes + " | oldest seconds=" + queue.OldestSeconds.ToString("F1", CultureInfo.InvariantCulture)
                        + " | send stalled seconds=" + queue.StalledSeconds.ToString("F1", CultureInfo.InvariantCulture)
                        + " | overflow=" + queue.Overflowed);
            text.AppendLine("Guest hand sync guards: recent pickup notes=" + HandEscrow.RecentTakeCount
                + " | oldest note seconds=" + HandEscrow.OldestRecentTakeAgeSeconds.ToString("F1", CultureInfo.InvariantCulture)
                + " | held reserved=" + HandEscrow.ReservedHeldCount
                + " | pending take items=" + HandEscrow.PendingTakeItemCount
                + " | deferred rejected=" + HandEscrow.DeferredRejectedItemCount
                + " | waiting to attach=" + HandEscrow.QueuedToHandCount);
            text.AppendLine("Object refresh: " + (string.IsNullOrEmpty(RecoveryLine) ? "not requested" : RecoveryLine));
            text.AppendLine("\nAdvanced snapshot schema: 2");
            AppendAdvancedDiagnostics(text);
            return text.ToString();
        }

        private void AppendAdvancedDiagnostics(StringBuilder text)
        {
            AppendSafeSection(text, "Runtime and shared game state", () =>
            {
                text.AppendLine("Platform: " + Application.platform + " | process: " + (Environment.Is64BitProcess ? "64-bit" : "32-bit")
                    + " | game scene active: " + InGameLevel() + " | borrowed world: " + GuestBorrowedWorld);
                text.AppendLine("Time scale: " + Time.timeScale.ToString("F2", CultureInfo.InvariantCulture)
                    + " | approximate FPS: " + (Time.unscaledDeltaTime > 0 ? (1f / Time.unscaledDeltaTime).ToString("F0", CultureInfo.InvariantCulture) : "n/a")
                    + " | sync active: " + _syncActive + " | tearing down: " + IsTearingDown);
                try
                {
                    double coin = CPlayerData.m_CoinAmountDouble;
                    text.AppendLine("Shared shop: day " + CPlayerData.m_CurrentDay + " | local shared balance: "
                        + coin.ToString("F2", CultureInfo.InvariantCulture) + " " + (EMoneyCurrencyType)CaptureCurrency().CurrencyId
                        + " | pending same-frame spend reservation: " + _pendingReduceThisFrame.ToString("F2", CultureInfo.InvariantCulture));
                }
                catch (Exception e) { text.AppendLine("Shared game values unavailable: " + e.GetType().Name); }
            });

            AppendSafeSection(text, "Compatibility fingerprints", () =>
            {
                text.AppendLine("Loaded plugin fingerprint: " + ModParity.PluginHash() + " | enum ID fingerprint: " + ModParity.EnumHash()
                    + " | custom card ID fingerprint: " + ModParity.CardsHash() + " | enum file matches running process: " + ModParity.RegistryFileMatchesRuntime());
                var plugins = ModParity.PluginList();
                text.AppendLine("Loaded plugins (GUID=version; " + plugins.Count + " total, first 100 shown):");
                int count = Math.Min(plugins.Count, 100);
                for (int i = 0; i < count; i++)
                    text.AppendLine("  " + plugins[i]);
                if (plugins.Count > count)
                    text.AppendLine("  ... " + (plugins.Count - count) + " more omitted");
            });

            AppendSafeSection(text, "Relevant diagnostics configuration", () =>
            {
                text.AppendLine("SendRateHz=" + ConfigValue(CoopPlugin.SendRateHz)
                    + " | AvatarsEnabled=" + ConfigValue(CoopPlugin.AvatarsEnabled)
                    + " | AutoSyncCardDatabase=" + ConfigValue(CoopPlugin.AutoSyncCardDatabase)
                    + " | AllowCrossBuildJoin=" + ConfigValue(CoopPlugin.AllowCrossBuildJoin));
                text.AppendLine("BoxSyncDebug=" + ConfigValue(CoopPlugin.BoxSyncDebug)
                    + " | PerfDebug=" + ConfigValue(CoopPlugin.PerfDebug)
                    + " | ArtificialLagMs=" + ConfigValue(CoopPlugin.ArtificialLagMs)
                    + " | ArtificialJitterMs=" + ConfigValue(CoopPlugin.ArtificialJitterMs));
            });

            AppendSafeSection(text, "Recent protocol traffic", () => MessageTrafficDiagnostics.AppendTo(text));
            AppendSafeSection(text, "Host-observed guest item outcomes", () => ItemTransferDiagnostics.AppendTo(text));
            AppendSafeSection(text, "Recent contained module failures", () => AppendHistory(text, ModuleGuard.RecentFailures.Snapshot()));
            AppendSafeSection(text, "Recent performance spikes", () => AppendHistory(text, PerfProbe.RecentSpikes.Snapshot()));
        }

        private static string ConfigValue<T>(BepInEx.Configuration.ConfigEntry<T> entry)
        {
            try
            {
                return entry == null ? "unavailable" : Convert.ToString(entry.Value, CultureInfo.InvariantCulture);
            }
            catch (Exception e) { return "unavailable (" + e.GetType().Name + ")"; }
        }

        private static void AppendSafeSection(StringBuilder text, string name, Action append)
        {
            text.AppendLine("\n" + name + ":");
            try
            {
                append();
            }
            catch (Exception e) { text.AppendLine("  Section unavailable: " + e.GetType().Name); }
        }

        private static void AppendHistory(StringBuilder text, string[] lines)
        {
            if (lines.Length == 0)
                text.AppendLine("  No recent entries.");
            else
                foreach (string line in lines)
                    text.AppendLine("  " + RedactSensitiveText(line));
        }

        private static string RedactSensitiveText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "none";
            string result = SteamIdPattern.Replace(value, "[SteamID]");
            result = IpAddressPattern.Replace(result, "[IP]");
            result = UserPathPattern.Replace(result, "[user path]");
            return SecretPattern.Replace(result, "$1$2[redacted]");
        }

        private void CaptureEndedSession(string reason)
        {
            if (Role != CoopRole.None || _net != null)
            {
                try
                {
                    _lastSessionReport = ConnectionSnapshot() + "Session ended: " + reason;
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("Could not capture session report: " + e.Message); }
            }
            _recovery.Reset();
            _receivedSaveBytes = _receivedBundleBytes = 0;
            _interactionLine = "";
            _interactionUntil = _nextInteractionNotice = 0;
        }

        public string BuildConnectionReport()
        {
            var text = new StringBuilder("CardShopCoop connection report\n");
            text.Append(ConnectionSnapshot());
            if (Role == CoopRole.None && !string.IsNullOrEmpty(_lastSessionReport))
                text.AppendLine("\nLast session before teardown:\n" + RedactSensitiveText(_lastSessionReport));
            text.AppendLine("\nRecent guest interaction notices (this game process):");
            foreach (string line in _interactionHistory.Snapshot())
                text.AppendLine(RedactSensitiveText(line));
            text.AppendLine("\nRecent mod warnings/errors (this game process):");
            foreach (string line in FileLog.RecentProblems.Snapshot())
                text.AppendLine(RedactSensitiveText(line));
            text.AppendLine("\nFull logs: BepInEx/LogOutput.log and BepInEx/CardShopCoop_*.log");
            text.AppendLine("Privacy: this copied report contains diagnostic counters, compatibility fingerprints, and loaded plugin GUID/version entries. Packet and save contents are excluded; common IDs, addresses, and secret-like fields in free text are redacted. Recent error text can still mention player names or other personal details. Review before sharing.");
            if (text.Length > MaxConnectionReportChars)
            {
                text.Length = MaxConnectionReportChars - 64;
                text.AppendLine("\n[Report truncated at the 48 KB safety limit.]");
            }
            return text.ToString();
        }
    }
}
