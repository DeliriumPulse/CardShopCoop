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

        /// <summary>Hash of EPL's custom-item ID registry ("none" when absent - a fresh
        /// install receives the host's copy at join, so absence is compatible).</summary>
        public static string EnumHash()
        {
            if (_enum != null) return _enum;
            try
            {
                string p = Path.Combine(Application.persistentDataPath, "PrefabLoader", "enum_values.json");
                _enum = File.Exists(p) ? Short(Sha1Bytes(File.ReadAllBytes(p))) : "none";
            }
            catch { _enum = "none"; }
            return _enum;
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
