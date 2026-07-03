using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;

namespace CardShopCoopLauncher
{
    /// <summary>
    /// One-click launcher: keeps CardShopCoop.dll up to date from GitHub Releases,
    /// then starts the game through Steam. CardShopCoop requires every player in a
    /// session to run the SAME version - this makes that automatic for a whole
    /// Discord's worth of players.
    /// </summary>
    internal static class Program
    {
        private const string Repo = "DeliriumPulse/CardShopCoop";
        private const string GameProcess = "Card Shop Simulator";
        private const string SteamAppId = "3070070";
        private const string DefaultGamePath =
            @"C:\Program Files (x86)\Steam\steamapps\common\TCG Card Shop Simulator";

        private static void Main()
        {
            Console.Title = "CardShopCoop Launcher";
            Banner();
            try
            {
                // net472 defaults to TLS 1.0 - GitHub requires 1.2+
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                string gamePath = LocateGame();
                string pluginDir = Path.Combine(gamePath, "BepInEx", "plugins");
                string dllPath = Path.Combine(pluginDir, "CardShopCoop.dll");

                if (!Directory.Exists(pluginDir))
                {
                    Fail("BepInEx isn't installed in that game folder yet.\n" +
                         "Install BepInEx 5 (x64) first, run the game once, then run this launcher again.");
                    return;
                }

                Version installed = InstalledVersion(dllPath);
                Info(installed != null
                    ? $"installed mod version: {installed}"
                    : "mod not installed yet - will download the latest");

                var latest = FetchLatestRelease();
                Info($"latest release: {latest.Tag}");

                if (installed == null || latest.Ver > installed)
                {
                    WaitForGameToClose();
                    Step($"updating {(installed == null ? "(fresh install)" : installed.ToString())} -> {latest.Tag} ...");
                    InstallDll(latest.ZipUrl, dllPath);
                    Ok($"CardShopCoop {latest.Tag} installed.");
                    if (!string.IsNullOrWhiteSpace(latest.Notes))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine("\nwhat's new:\n" + latest.Notes.Trim() + "\n");
                        Console.ResetColor();
                    }
                }
                else
                {
                    Ok("you're already on the latest version.");
                }

                Step("launching the game through Steam...");
                Process.Start(new ProcessStartInfo("steam://rungameid/" + SteamAppId) { UseShellExecute = true });
                Ok("done - see you in the shop! (this window closes in 5s)");
                Thread.Sleep(5000);
            }
            catch (Exception e)
            {
                Fail("something went wrong:\n" + e.Message +
                     "\n\nYou can always update manually from:\nhttps://github.com/" + Repo + "/releases/latest");
            }
        }

        // ---------------- game location ----------------

        private static string LocateGame()
        {
            // remembered path from a previous run (lives next to the exe)
            string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.cfg");
            if (File.Exists(cfg))
            {
                string saved = File.ReadAllText(cfg).Trim();
                if (Directory.Exists(saved)) return saved;
            }
            if (Directory.Exists(DefaultGamePath))
            {
                File.WriteAllText(cfg, DefaultGamePath);
                return DefaultGamePath;
            }
            // walk every Steam library for the app manifest
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
                            if (Directory.Exists(candidate))
                            {
                                File.WriteAllText(cfg, candidate);
                                return candidate;
                            }
                        }
                    }
                }
            }
            catch { }
            // last resort: ask once and remember
            Console.Write("\nCouldn't find the game - paste your TCG Card Shop Simulator folder path:\n> ");
            string typed = (Console.ReadLine() ?? "").Trim().Trim('"');
            if (!Directory.Exists(typed)) throw new DirectoryNotFoundException("that folder doesn't exist");
            File.WriteAllText(cfg, typed);
            return typed;
        }

        // ---------------- versions & release ----------------

        private static Version InstalledVersion(string dllPath)
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

        private sealed class Release
        {
            public string Tag;
            public Version Ver;
            public string ZipUrl;
            public string Notes;
        }

        private static Release FetchLatestRelease()
        {
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CardShopCoopLauncher");
                string json = http.GetStringAsync(
                    $"https://api.github.com/repos/{Repo}/releases/latest").GetAwaiter().GetResult();

                string tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                // the main zip, not the Thunderstore package
                string zip = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"")
                    .Cast<Match>().Select(m => m.Groups[1].Value)
                    .FirstOrDefault(u => u.EndsWith(".zip") && !u.Contains("Thunderstore"));
                string body = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value
                    .Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\\"", "\"");

                if (tag.Length == 0 || zip == null)
                    throw new InvalidOperationException("couldn't read the latest release from GitHub");
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver))
                    throw new InvalidOperationException("unrecognized version tag: " + tag);
                return new Release { Tag = tag, Ver = ver, ZipUrl = zip, Notes = body };
            }
        }

        private static void InstallDll(string zipUrl, string dllPath)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "CardShopCoop-update.zip");
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CardShopCoopLauncher");
                File.WriteAllBytes(tmp, http.GetByteArrayAsync(zipUrl).GetAwaiter().GetResult());
            }
            using (var zip = ZipFile.OpenRead(tmp))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name == "CardShopCoop.dll");
                if (entry == null) throw new InvalidOperationException("release zip has no CardShopCoop.dll");
                entry.ExtractToFile(dllPath, overwrite: true);
            }
            try { File.Delete(tmp); } catch { }
        }

        private static void WaitForGameToClose()
        {
            while (Process.GetProcessesByName(GameProcess).Length > 0)
            {
                Warn("the game is running - close it so the mod file can be replaced. Waiting...");
                Thread.Sleep(3000);
            }
        }

        // ---------------- console dressing ----------------

        private static void Banner()
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  ____              _ ____  _                  ____                  ");
            Console.WriteLine(" / ___|__ _ _ __ __| / ___|| |__   ___  _ __  / ___|___   ___  _ __  ");
            Console.WriteLine("| |   / _` | '__/ _` \\___ \\| '_ \\ / _ \\| '_ \\| |   / _ \\ / _ \\| '_ \\ ");
            Console.WriteLine("| |__| (_| | | | (_| |___) | | | | (_) | |_) | |__| (_) | (_) | |_) |");
            Console.WriteLine(" \\____\\__,_|_|  \\__,_|____/|_| |_|\\___/| .__/ \\____\\___/ \\___/| .__/ ");
            Console.WriteLine("        run one shop together          |_|   auto-updater     |_|    ");
            Console.ResetColor();
            Console.WriteLine();
        }

        private static void Info(string s) { Console.WriteLine("  " + s); }
        private static void Step(string s) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine("> " + s); Console.ResetColor(); }
        private static void Ok(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("+ " + s); Console.ResetColor(); }
        private static void Warn(string s) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine("! " + s); Console.ResetColor(); }

        private static void Fail(string s)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("x " + s);
            Console.ResetColor();
            Console.WriteLine("\npress any key to close");
            Console.ReadKey();
        }
    }
}
