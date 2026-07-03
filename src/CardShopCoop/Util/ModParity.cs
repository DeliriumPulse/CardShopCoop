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

        /// <summary>Hash of the loaded BepInEx plugin set (guid=version, sorted).</summary>
        public static string PluginHash()
        {
            if (_plugins != null) return _plugins;
            try
            {
                var parts = new List<string>();
                foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                    parts.Add(kv.Key + "=" + kv.Value.Metadata.Version);
                parts.Sort(StringComparer.Ordinal);
                _plugins = Short(Sha1(string.Join(";", parts)));
            }
            catch { _plugins = "err"; }
            return _plugins;
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
                if (File.Exists(p))
                {
                    var current = File.ReadAllBytes(p);
                    if (SameBytes(current, hostBytes))
                        return "card database already synced - RESTART the game, then join again";
                    string bak = p + ".coopbak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(p, bak, overwrite: true);
                    PruneBackups(p);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                }
                File.WriteAllBytes(p, hostBytes);
                return "card database synced from host (your old file was backed up) - RESTART the game, then join again";
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("enum sync failed: " + e.Message);
                return "could not update the card database automatically - copy the host's enum_values.json manually (see mod page)";
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
