using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CardShopCoopLauncher
{
    /// <summary>All the update plumbing, UI-free so the window stays readable.</summary>
    internal static class Updater
    {
        public const string Repo = "DeliriumPulse/CardShopCoop";
        public const string GameProcess = "Card Shop Simulator";
        public const string SteamAppId = "3070070";
        private const string DefaultGamePath =
            @"C:\Program Files (x86)\Steam\steamapps\common\TCG Card Shop Simulator";

        public sealed class Release
        {
            public string Tag;
            public Version Ver;
            public string ZipUrl;
            public string Notes;
        }

        static Updater()
        {
            // net472 defaults to TLS 1.0 - GitHub requires 1.2+
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        private static string CfgPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.cfg");

        public static string SavedGamePath()
        {
            if (File.Exists(CfgPath))
            {
                string saved = File.ReadAllText(CfgPath).Trim();
                if (Directory.Exists(saved)) return saved;
            }
            return null;
        }

        public static void SaveGamePath(string path) => File.WriteAllText(CfgPath, path);

        /// <summary>Default path, then every Steam library; null when not found.</summary>
        public static string AutoLocateGame()
        {
            string saved = SavedGamePath();
            if (saved != null) return saved;
            if (Directory.Exists(DefaultGamePath)) { SaveGamePath(DefaultGamePath); return DefaultGamePath; }
            try
            {
                string steam = (string)Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
                if (steam != null)
                {
                    string vdf = Path.Combine(steam.Replace('/', '\\'), "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        var libs = Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")
                            .Cast<Match>().Select(m => m.Groups[1].Value.Replace("\\\\", "\\"));
                        foreach (var lib in libs)
                        {
                            string candidate = Path.Combine(lib, "steamapps", "common", "TCG Card Shop Simulator");
                            if (Directory.Exists(candidate)) { SaveGamePath(candidate); return candidate; }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static Version InstalledVersion(string dllPath)
        {
            if (!File.Exists(dllPath)) return null;
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(dllPath);
                if (Version.TryParse(fv.FileVersion, out var v) && (v.Major + v.Minor + v.Build) > 0)
                    return new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
            }
            catch { }
            return null; // unknown = treat as outdated
        }

        public static async Task<Release> FetchLatestRelease()
        {
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CardShopCoopLauncher");
                string json = await http.GetStringAsync(
                    $"https://api.github.com/repos/{Repo}/releases/latest").ConfigureAwait(false);

                string tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                string zip = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"")
                    .Cast<Match>().Select(m => m.Groups[1].Value)
                    .FirstOrDefault(u => u.EndsWith(".zip") && u.Contains("CardShopCoop-")
                                         && !u.Contains("Thunderstore") && !u.Contains("Launcher"));
                string body = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value
                    .Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\\"", "\"");

                if (tag.Length == 0 || zip == null)
                    throw new InvalidOperationException("couldn't read the latest release from GitHub");
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver))
                    throw new InvalidOperationException("unrecognized version tag: " + tag);
                return new Release { Tag = tag, Ver = ver, ZipUrl = zip, Notes = body };
            }
        }

        public static async Task InstallDll(string zipUrl, string dllPath, IProgress<double> progress)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "CardShopCoop-update.zip");
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CardShopCoopLauncher");
                using (var resp = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = File.Create(tmp))
                    {
                        var buf = new byte[81920];
                        long done = 0;
                        int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                        {
                            dst.Write(buf, 0, n);
                            done += n;
                            if (total > 0) progress?.Report(done * 100.0 / total);
                        }
                    }
                }
            }
            using (var zip = ZipFile.OpenRead(tmp))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name == "CardShopCoop.dll");
                if (entry == null) throw new InvalidOperationException("release zip has no CardShopCoop.dll");
                entry.ExtractToFile(dllPath, overwrite: true);
            }
            try { File.Delete(tmp); } catch { }
        }

        public static bool GameIsRunning() =>
            Process.GetProcessesByName(GameProcess).Length > 0;

        public static void LaunchGame() =>
            Process.Start(new ProcessStartInfo("steam://rungameid/" + SteamAppId) { UseShellExecute = true });
    }
}
