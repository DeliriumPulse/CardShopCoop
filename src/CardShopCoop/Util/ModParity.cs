using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Everything in this mod syncs by game IDs, which is only safe when both players run
    /// the SAME mod set (content mods define the ID space). These hashes are exchanged in
    /// the Hello handshake; mismatches are rejected with a readable reason instead of
    /// silently corrupting the shared world.
    /// </summary>
    public static class ModParity
    {
        private static string _plugins;
        private static string _enum;
        private static string _cards;

        /// <summary>Hash of the custom-card ID space minted by CreateCards: each
        /// MonsterConfig's "Monster Type = Monster Type ID" mapping, sorted. This is
        /// exactly what WriteCard/ReadCard sync depends on. The plugin and enum hashes
        /// do NOT cover it - custom EMonsterType cards aren't in EPL's enum_values.json -
        /// so without this, two players could pass the handshake and then silently show
        /// the wrong card. Cosmetic fields (art, stats, description) are excluded on
        /// purpose so a shared card with tweaked flavor still matches.</summary>
        public static string CardsHash()
        {
            if (_cards != null) return _cards;
            try
            {
                var entries = CardEntries();
                _cards = entries.Count == 0 ? "none" : Short(Sha1(string.Join(";", entries)));
            }
            catch { _cards = "err"; }
            return _cards;
        }

        /// <summary>The exact sorted "Monster Type=Monster Type ID" identity strings CardsHash
        /// hashes, exposed as a list so the handshake can SHOW the mismatch, not just reject it.
        /// Both come from CardEntries() so the list a player sees can never disagree with the
        /// hash that gated them. CoopCore calls this by name.</summary>
        public static List<string> CardsList()
        {
            try { return CardEntries(); }
            catch { return new List<string>(); }
        }

        /// <summary>The .ini-derived custom-card identity strings, sorted - the single source
        /// both CardsHash and CardsList read so hash and list are always in step.</summary>
        private static List<string> CardEntries()
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "patchers", "CreateCardsPreloader", "MonsterConfigs");
            var entries = new List<string>();
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*.ini"))
                {
                    string name = null, id = null;
                    foreach (var line in File.ReadAllLines(f))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        if (k.Equals("Monster Type", StringComparison.OrdinalIgnoreCase)) name = line.Substring(eq + 1).Trim();
                        else if (k.Equals("Monster Type ID", StringComparison.OrdinalIgnoreCase)) id = line.Substring(eq + 1).Trim();
                    }
                    if (name != null && id != null) entries.Add(name + "=" + id);
                }
            }
            entries.Sort(StringComparer.Ordinal);
            return entries;
        }

        /// <summary>Hash of the loaded BepInEx plugin set (guid=version, sorted).</summary>
        public static string PluginHash()
        {
            if (_plugins != null) return _plugins;
            try { _plugins = Short(Sha1(string.Join(";", PluginEntries()))); }
            catch { _plugins = "err"; }
            return _plugins;
        }

        /// <summary>The same sorted "guid=version" entries PluginHash hashes, exposed as a list
        /// so a mismatch can be shown side-by-side instead of just rejected. Shares PluginEntries()
        /// with PluginHash so the two never disagree. CoopCore calls this by name.</summary>
        public static List<string> PluginList()
        {
            try { return PluginEntries(); }
            catch { return new List<string>(); }
        }

        /// <summary>The loaded plugin set as sorted "guid=version" strings - the single source
        /// both PluginHash and PluginList read.</summary>
        private static List<string> PluginEntries()
        {
            var parts = new List<string>();
            foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                parts.Add(kv.Key + "=" + kv.Value.Metadata.Version);
            parts.Sort(StringComparer.Ordinal);
            return parts;
        }

        /// <summary>EPL's custom-item ID registry on disk. The hash is cached for the
        /// whole session ON PURPOSE: after an auto-sync rewrites the file, the game must
        /// restart before the new IDs are actually loaded, and the stale cached hash
        /// keeps the host politely refusing a premature rejoin until then.</summary>
        public static string EnumFilePath()
        {
            return Path.Combine(Application.persistentDataPath, "PrefabLoader", "enum_values.json");
        }

        public static string EnumHash()
        {
            if (_enum != null) return _enum;
            try
            {
                string p = EnumFilePath();
                _enum = File.Exists(p) ? Short(Sha1Bytes(File.ReadAllBytes(p))) : "none";
            }
            catch { _enum = "none"; }
            return _enum;
        }

        /// <summary>Install the host's registry over ours, keeping timestamped backups
        /// (the newest 3). Returns a user-facing status line.</summary>
        public static string InstallEnumFile(byte[] hostBytes)
        {
            string p = EnumFilePath();
            try
            {
                string bak = null;
                if (File.Exists(p))
                {
                    var current = File.ReadAllBytes(p);
                    if (SameBytes(current, hostBytes))
                        return "card database already synced - RESTART the game, then join again";
                    bak = p + ".coopbak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(p, bak, overwrite: true);
                    PruneBackups(p);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                }
                File.WriteAllBytes(p, hostBytes);
                // Drop a marker so the mod KNOWS the machine-global registry is now the HOST's,
                // not the guest's own. Without a restore path a mismatched-enum join used to
                // silently brick every modded SOLO save ("data lost") until the file was fixed
                // by hand; HostEnumInstalled() reads this marker to offer a one-click restore,
                // RestoreEnumBackup() clears it. We record the backup just made so the human (and
                // the restore) can find the guest's own file.
                WriteEnumMarker(bak);
                return "card database synced from host (your old file was backed up) - RESTART the game, then join again";
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("enum sync failed: " + e.Message);
                return "could not update the card database automatically - copy the host's enum_values.json manually (see mod page)";
            }
        }

        /// <summary>Marker written beside enum_values.json while the host's registry is on loan
        /// in place of the guest's own. Its mere existence is the signal that a restore is owed.</summary>
        private static string EnumMarkerPath()
        {
            return EnumFilePath() + ".hostlend";
        }

        private static void WriteEnumMarker(string newestBackup)
        {
            try
            {
                string content =
                    (newestBackup != null ? Path.GetFileName(newestBackup) : "(no prior registry - none to back up)")
                    + Environment.NewLine + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(EnumMarkerPath(), content);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("enum marker write failed: " + e.Message); }
        }

        /// <summary>True while the host's registry is installed over the guest's own (the marker
        /// exists). Cheap File.Exists on purpose - it's polled rarely (a UI prompt), so there's no
        /// caching to go stale after a restore. CoopCore reads this to offer the restore action.</summary>
        public static bool HostEnumInstalled()
        {
            try { return File.Exists(EnumMarkerPath()); }
            catch { return false; }
        }

        /// <summary>Undo a host-enum lend: put the guest's OWN registry back so their modded solo
        /// saves load again. Finds the newest .coopbak-* (the file we set aside at install time),
        /// first copies the CURRENT (host's) file to .hostcopy so nothing is ever destroyed, then
        /// restores the backup over enum_values.json and clears the marker. The game reads the
        /// registry once at startup, so <paramref name="message"/> tells the user to restart
        /// before loading solo saves. Returns false (with an explaining message) when no backup
        /// exists to restore. CoopCore calls this by name.</summary>
        public static bool RestoreEnumBackup(out string message)
        {
            string p = EnumFilePath();
            try
            {
                var dir = Path.GetDirectoryName(p);
                string newest = null;
                if (Directory.Exists(dir))
                {
                    var baks = Directory.GetFiles(dir, Path.GetFileName(p) + ".coopbak-*");
                    Array.Sort(baks, StringComparer.Ordinal); // timestamp suffix sorts oldest-first
                    if (baks.Length > 0) newest = baks[baks.Length - 1];
                }
                if (newest == null)
                {
                    // Nothing to hand back (e.g. there was no registry before the lend). Leave the
                    // marker so the prompt can reappear; explain the manual path.
                    message = "no backup of your card database was found to restore - if your solo saves won't load, put your own enum_values.json back by hand (see mod page)";
                    return false;
                }
                // Preserve the host's installed file first so a restore is never a one-way loss.
                if (File.Exists(p))
                    File.Copy(p, p + ".hostcopy", overwrite: true);
                File.Copy(newest, p, overwrite: true);
                try { File.Delete(EnumMarkerPath()); } catch { }
                message = "your card database was restored from backup - RESTART the game before loading your solo saves";
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("enum restore failed: " + e.Message);
                message = "could not restore your card database automatically - put your own enum_values.json backup back by hand (see mod page)";
                return false;
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void PruneBackups(string basePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(basePath);
                var baks = Directory.GetFiles(dir, Path.GetFileName(basePath) + ".coopbak-*");
                Array.Sort(baks, StringComparer.Ordinal); // timestamp suffix sorts oldest-first
                for (int i = 0; i < baks.Length - 3; i++) File.Delete(baks[i]);
            }
            catch { }
        }

        private static string Sha1(string s) { return Sha1Bytes(Encoding.UTF8.GetBytes(s)); }

        private static string Sha1Bytes(byte[] data)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }

        private static string Short(string hex) { return hex.Length > 16 ? hex.Substring(0, 16) : hex; }
    }
}
