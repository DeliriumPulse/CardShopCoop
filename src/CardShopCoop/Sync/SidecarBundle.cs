using System;
using System.Collections.Generic;
using System.IO;

namespace CardShopCoop.Sync
{
    internal sealed class SidecarBundleEntry
    {
        public string Path;
        public int Offset;
        public int Length;
    }

    /// <summary>Validate the whole file table before any destination file is changed.
    /// Keep offsets into the received buffer instead of copying every file at once.</summary>
    internal static class SidecarBundle
    {
        public static List<SidecarBundleEntry> Read(byte[] bundle)
        {
            var entries = new List<SidecarBundleEntry>();
            if (bundle == null || bundle.Length == 0)
                return entries;
            if (bundle.Length > 64 * 1024 * 1024)
                throw new InvalidDataException("Mod data exceeds the bundle limit");
            using (var stream = new MemoryStream(bundle, writable: false))
            using (var reader = new BinaryReader(stream))
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > 4096)
                    throw new InvalidDataException("Invalid mod-data file count");
                for (int i = 0; i < count; i++)
                {
                    string path = reader.ReadString();
                    int length = reader.ReadInt32();
                    if (path.Length > 4096 || length < 0 || length > stream.Length - stream.Position)
                        throw new InvalidDataException("Incomplete or invalid mod-data file entry " + i);
                    entries.Add(new SidecarBundleEntry
                    {
                        Path = path,
                        Offset = checked((int)stream.Position),
                        Length = length
                    });
                    stream.Position += length;
                }
                if (stream.Position != stream.Length)
                    throw new InvalidDataException("Unexpected bytes after the mod-data file table");
            }
            return entries;
        }
    }
}
