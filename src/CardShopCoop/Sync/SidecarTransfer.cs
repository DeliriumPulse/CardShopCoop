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
        public static byte[] BuildBundle(int hostSlot)
        {
            string root = Application.persistentDataPath;
            var files = new List<string>();
            var slotRx = new Regex($@"(_|Release){hostSlot}(_|\.|$)");
            foreach (string dir in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(dir) == "Screenshots" || Path.GetFileName(dir) == "Unity") continue;
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(f);
                    if (slotRx.IsMatch(name) || name == "enum_values.json")
                        files.Add(f);
                }
            }

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
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
            if (bundle == null || bundle.Length < 4) return;
            string root = Application.persistentDataPath;
            var renameRx = new Regex($@"(?<=_|Release){hostSlot}(?=_|\.|$)");
            int applied = 0, skipped = 0;

            using (var br = new BinaryReader(new MemoryStream(bundle, writable: false)))
            {
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string rel = br.ReadString();
                    int len = br.ReadInt32();
                    byte[] data = br.ReadBytes(len);

                    string dir = Path.GetDirectoryName(rel) ?? "";
                    string name = Path.GetFileName(rel);

                    if (name == "enum_values.json")
                    {
                        // EPL's machine-global custom-item ID registry. Overwriting a
                        // DIFFERENT existing registry would scramble the modded items in the
                        // client's own solo saves, so only install it where none exists yet
                        // (the fresh second-PC case, which is the one that matters).
                        string target = Path.Combine(root, dir, name);
                        if (!File.Exists(target))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            File.WriteAllBytes(target, data);
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
                    string path = Path.Combine(root, dir, newName);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if (File.Exists(path) && !File.Exists(path + ".coopbak"))
                        File.Copy(path, path + ".coopbak"); // one-time backup of whatever was there
                    File.WriteAllBytes(path, data);
                    applied++;
                }
            }
            CoopPlugin.Log.LogInfo($"Sidecar bundle applied: {applied} files (slot {hostSlot} -> {clientSlot}), {skipped} skipped");
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
