using CardShopCoop.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Many mods (PTCGO Economics, Shop Overhaul, EPL, ...) keep per-save data in their own
    /// files under the save folder, named like ShopOverhaul_0.json / savedGames_Release0.json.
    /// The base-save transfer alone leaves the client's world without prices, phone state,
    /// EPL item data etc. This bundles every host file whose name carries the host's slot
    /// number (plus EPL's global enum_values.json) and rewrites the slot digit to the
    /// client's co-op slot on arrival.
    /// Bundle format: [int fileCount] then per file [string relPath][int len][bytes].
    /// </summary>
    public static class SidecarTransfer
    {
        private static bool TryGetSafePath(string root, string rel, out string full)
        {
            full = null;
            if (string.IsNullOrEmpty(rel) || Path.IsPathRooted(rel) || rel.IndexOf(':') >= 0)
                return false;

            string[] parts = rel.Split(new[] { '/', '\\' }, StringSplitOptions.None);
            foreach (string part in parts)
                if (part == "..")
                    return false;

            string normalizedRoot = Path.GetFullPath(root);
            if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                normalizedRoot += Path.DirectorySeparatorChar;
            full = Path.GetFullPath(Path.Combine(normalizedRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectUnsafe(string rel)
        {
            CoopPlugin.Log.LogWarning("sidecar: rejecting unsafe path '" + rel + "'");
        }

        public static void ApplyBundleAsync(byte[] bundle, int hostSlot, int clientSlot, int sessionGen,
            Action completed, Action<Exception> failed)
        {
            string root = Application.persistentDataPath;
            new System.Threading.Thread(() =>
            {
                try
                {
                    lock (CoopCore.JoinTransferLock)
                    {
                        if (!CoopCore.IsSessionGeneration(sessionGen))
                        {
                            CoopPlugin.Log.LogInfo("coop: stale sidecar apply discarded before disk write");
                            return;
                        }
                        ApplyBundle(bundle, hostSlot, clientSlot, root);
                    }
                    if (CoopCore.IsSessionGeneration(sessionGen))
                        CoopCore.TryEnqueueMainThread(() =>
                        {
                            if (CoopCore.IsSessionGeneration(sessionGen))
                                completed?.Invoke();
                        });
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError("coop: sidecar apply worker failed: " + e);
                    if (CoopCore.IsSessionGeneration(sessionGen))
                        CoopCore.TryEnqueueMainThread(() =>
                        {
                            if (CoopCore.IsSessionGeneration(sessionGen))
                                failed(e);
                        });
                }
            })
            {
                IsBackground = true,
                Name = "CoopSidecarApply"
            }.Start();
        }

        public static byte[] BuildBundle(int hostSlot)
        {
            string root = Application.persistentDataPath;
            var files = new List<string>();
            var slotRx = new Regex($@"(_|Release){hostSlot}(_|\.|$)");
            foreach (string dir in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(dir) == "Screenshots" || Path.GetFileName(dir) == "Unity")
                    continue;
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(f);
                    if (slotRx.IsMatch(name) || name == "enum_values.json")
                        files.Add(f);
                }
            }

            using (var ms = new MemoryStream())
            using (var bw = new NetWriter(ms))
            {
                bw.Write(files.Count);
                foreach (string f in files)
                {
                    string rel = f.Substring(root.Length + 1).Replace('\\', '/');
                    byte[] data = File.ReadAllBytes(f);
                    bw.Write(rel);
                    bw.Write(data.Length);
                    bw.Write(data);
                }
                bw.Flush();
                CoopPlugin.Log.LogInfo($"Sidecar bundle: {files.Count} mod files, {ms.Length / 1024} KB");
                return ms.ToArray();
            }
        }

        public static void ApplyBundle(byte[] bundle, int hostSlot, int clientSlot)
        {
            if (bundle == null || bundle.Length == 0)
                return;
            ApplyBundle(bundle, hostSlot, clientSlot, Application.persistentDataPath);
        }

        private static void ApplyBundle(byte[] bundle, int hostSlot, int clientSlot, string root)
        {
            if (bundle == null || bundle.Length == 0)
                return;
            var entries = SidecarBundle.Read(bundle);
            var renameRx = new Regex($@"(?<=_|Release){hostSlot}(?=_|\.|$)");
            int applied = 0, skipped = 0;

            foreach (var entry in entries)
            {
                string rel = entry.Path;
                byte[] data = new byte[entry.Length];
                Buffer.BlockCopy(bundle, entry.Offset, data, 0, entry.Length);

                if (!TryGetSafePath(root, rel, out string originalPath))
                {
                    RejectUnsafe(rel);
                    skipped++;
                    continue;
                }
                string dir = Path.GetDirectoryName(rel) ?? "";
                string name = Path.GetFileName(rel);

                if (name == "enum_values.json")
                {
                    // EPL's machine-global custom-item ID registry. Overwriting a
                    // DIFFERENT existing registry would scramble the modded items in the
                    // client's own solo saves, so only install it where none exists yet
                    // (the fresh second-PC case, which is the one that matters).
                    string target = originalPath;
                    if (!File.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        AtomicWrite(target, data);
                        applied++;
                    }
                    else if (!BytesEqual(File.ReadAllBytes(target), data))
                    {
                        skipped++;
                        CoopPlugin.Log.LogWarning(
                            "enum_values.json differs from the host's. Modded item IDs may not line up. " +
                            "For perfect fidelity on a dedicated co-op PC, delete LocalLow/OPNeonGames/" +
                            "Card Shop Simulator/PrefabLoader/enum_values.json once (while not using solo modded saves) and rejoin.");
                    }
                    continue;
                }

                string newName = renameRx.Replace(name, clientSlot.ToString());
                string rewrittenRel = string.IsNullOrEmpty(dir) ? newName : Path.Combine(dir, newName);
                if (!TryGetSafePath(root, rewrittenRel, out string path))
                {
                    RejectUnsafe(rel);
                    skipped++;
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path) && !File.Exists(path + ".coopbak"))
                    File.Copy(path, path + ".coopbak"); // one-time backup of whatever was there
                AtomicWrite(path, data);
                applied++;
            }
            CoopPlugin.Log.LogInfo($"Sidecar bundle applied: {applied} files (slot {hostSlot} -> {clientSlot}), {skipped} skipped");
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static void AtomicWrite(string path, byte[] data)
        {
            string temp = path + ".cooptmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temp, data);
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
    }
}
