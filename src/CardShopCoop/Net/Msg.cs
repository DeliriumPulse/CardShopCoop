using System;
using System.IO;

namespace CardShopCoop.Net
{
    public enum MsgType : byte
    {
        Hello = 1,        // client -> host: version, playerName
        Welcome = 2,      // host -> client: version, hostName, saveSize
        SaveChunk = 3,    // host -> client: offset, bytes
        SaveDone = 4,     // host -> client: totalLength
        PlayerState = 5,  // both ways: pos, yaw, speed
        CoinSet = 6,      // host -> client: double amount
        DayTime = 7,      // host -> client: day, hour, minute
        Emote = 8,        // both ways: emote id
        Ping = 9,
        Pong = 10,
        Bye = 11,
        ShelfDelta = 12,   // host -> client: authoritative compartment states
        ShelfRequest = 13, // client -> host: player restocked/took items, please apply
        PriceList = 14,    // host -> client: full item price table
        BundleChunk = 15,  // host -> client: mod sidecar data bundle
        BundleDone = 16,
        ProgressSet = 17,  // host -> client: shop exp, shop level, fame
        Activity = 18,     // both ways: short activity ping ("opening a pack!")
    }

    /// <summary>One received message, already reassembled from the wire.</summary>
    public struct InMsg
    {
        public int ConnId;
        public MsgType Type;
        public byte[] Payload;
    }

    /// <summary>Builders/parsers for message payloads. Wire format per frame:
    /// [int32 payloadLen+1][byte MsgType][payload]. All little-endian via BinaryWriter.</summary>
    public static class Msg
    {
        public static byte[] Build(MsgType type, Action<BinaryWriter> write = null)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(0);              // frame length placeholder
                bw.Write((byte)type);
                write?.Invoke(bw);
                bw.Flush();
                long end = ms.Position;
                ms.Position = 0;
                bw.Write((int)(end - 4)); // bytes after the length field
                bw.Flush();
                return ms.ToArray();
            }
        }

        public static BinaryReader Reader(byte[] payload)
        {
            return new BinaryReader(new MemoryStream(payload, writable: false));
        }
    }
}
