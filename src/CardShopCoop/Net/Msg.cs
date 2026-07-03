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
        EconContrib = 19,  // client -> host: forwarded money/XP/fame earned by the joiner
        CardDelta = 20,    // both ways: a card entered/left the shared collection
        NpcState = 21,     // host -> client: batched customer/worker puppet states
        ServeRequest = 22, // client -> host: joiner works the register (scan / take payment)
        ServeStatus = 23,  // host -> client: register feedback (scanned n/m, paid, no customer)
        CardShelfDelta = 24,   // host -> client: authoritative card display slots
        CardShelfRequest = 25, // client -> host: joiner placed/removed a display card
        CardPriceSet = 26,     // both ways: a card's marked price changed
        RegisterState = 27,    // host -> client: per-counter checkout state + cart items
        ScanEcho = 28,         // host -> client: a scan landed (fills the vanilla checkout UI)
        Roster = 29,           // host -> clients: connId->name table for relayed peers
        RelayState = 30,       // host -> clients: another client's PlayerState [senderId + state]
        RelayTag = 31,         // host -> clients: another client's emote/activity [senderId + kind]
        ObjMoveDelta = 32,     // host -> client: authoritative placed-object transforms
        ObjMoveRequest = 33,   // client -> host: joiner moved a shelf/counter/decoration
        BoxState = 34,         // host -> client: full loose-box population snapshot
        BoxRequest = 35,       // client -> host: joiner's box edits (dispense/carry/trash)
        OrderRequest = 36,     // client -> host: joiner bought restock (spawn officially)
        ShopName = 37,         // host -> client: the shop's name
        ItemPriceContrib = 38, // client -> host: joiner set an item price
        LightState = 39,       // host -> client: full LightTimeData (sky phase, timers)
        PopState = 40,         // host -> client: placed-object population roster (all kinds)
        FurnitureOrder = 41,   // client -> host: joiner bought furniture (spawn officially)
        BoxRemoved = 42,       // client -> host: joiner trashed a loose box (destroy officially)
        LicenseUnlock = 43,    // both ways: a product license was purchased (itemType+size identity)
        LicenseState = 44,     // host -> client: full unlocked-license set (identity-keyed)
        StaffOp = 45,          // client -> host: hire/fire/manage a worker
        StaffState = 46,       // host -> client: hired roster + worker settings
        ShopOp = 47,           // client -> host: pay bill / unlock room / flip a sign
        ShopState = 48,        // host -> client: bills + room unlocks + sign states
        SettingsOp = 49,       // client -> host: deco / game event / counter toggles / table numbers
        SettingsState = 50,    // host -> client: those settings, authoritative
        MarketState = 51,      // host -> client: market % changes + price history (one shared market)
        ReportState = 52,      // host -> client: end-of-day report + customer reviews
        ContainerOp = 53,      // client -> host: container edits (storage/donation/openers/box bank)
        ContainerState = 54,   // host -> client: container contents, authoritative
        TournamentState = 55,  // host -> client: tournament schedule/rounds/signups
        GradingOp = 56,        // client -> host: joiner submits cards for grading (matures on host days)
        GradingState = 57,     // host -> client: pending grading submissions
        TradeOp = 58,          // client -> host: joiner accepts/declines a counter trade/sell-in
        TradeState = 59,       // host -> client: live trade/sell-in offer at a counter
        TableState = 60,       // host -> client: play-table card layout digest (visuals)
        CardBoxOp = 61,        // client -> host: joiner collects/moves a graded-returns card box
        CardBoxState = 62,     // host -> client: card packaging box population (graded returns)
        EnumSync = 63,         // host -> client: the host's enum_values.json (card-ID registry)
        Toast = 64,            // host -> client: one-line on-screen notice
        CatalogDigest = 65,    // client -> host: restock catalog identities (mismatch diagnosis)
        FurnBoxOp = 66,        // client -> host: furniture-box carry/place/destroy ops
        FurnBoxState = 67,     // host -> client: furniture delivery box population
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
        // One builder per thread, reused forever: Build runs ~30x/second in a session
        // (15Hz states + engine deltas) and a fresh MemoryStream+writer per message was
        // a steady GC drip that only existed while connected.
        [ThreadStatic] private static MemoryStream _buildMs;
        [ThreadStatic] private static BinaryWriter _buildBw;

        public static byte[] Build(MsgType type, Action<BinaryWriter> write = null)
        {
            if (_buildMs == null)
            {
                _buildMs = new MemoryStream(4096);
                _buildBw = new BinaryWriter(_buildMs);
            }
            var ms = _buildMs;
            var bw = _buildBw;
            ms.SetLength(0);
            ms.Position = 0;
            bw.Write(0);              // frame length placeholder
            bw.Write((byte)type);
            write?.Invoke(bw);
            bw.Flush();
            long end = ms.Position;
            ms.Position = 0;
            bw.Write((int)(end - 4)); // bytes after the length field
            bw.Flush();
            return ms.ToArray();      // the one remaining copy: transports own the array
        }

        public static BinaryReader Reader(byte[] payload)
        {
            return new BinaryReader(new MemoryStream(payload, writable: false));
        }

        /// <summary>World/bundle transfers are gzipped: the EPL sidecar json compresses
        /// ~5-10x, turning a minutes-long relay transfer into seconds.</summary>
        public static byte[] Gzip(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest))
                    gz.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        public static byte[] Gunzip(byte[] data)
        {
            using (var src = new MemoryStream(data, writable: false))
            using (var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                gz.CopyTo(dst);
                return dst.ToArray();
            }
        }

        /// <summary>Shared CardData wire format (used by CardDelta, CardShelfDelta, CardPriceSet).</summary>
        public static void WriteCard(BinaryWriter bw, CardData card)
        {
            bw.Write((int)card.expansionType);
            bw.Write((int)card.monsterType);
            bw.Write((int)card.borderType);
            bw.Write(card.isFoil);
            bw.Write(card.isDestiny);
            bw.Write(card.isChampionCard);
            bw.Write(card.isNew);
            bw.Write(card.cardGrade);
            bw.Write(card.gradedCardIndex);
        }

        public static CardData ReadCard(BinaryReader br)
        {
            return new CardData
            {
                expansionType = (ECardExpansionType)br.ReadInt32(),
                monsterType = (EMonsterType)br.ReadInt32(),
                borderType = (ECardBorderType)br.ReadInt32(),
                isFoil = br.ReadBoolean(),
                isDestiny = br.ReadBoolean(),
                isChampionCard = br.ReadBoolean(),
                isNew = br.ReadBoolean(),
                cardGrade = br.ReadInt32(),
                gradedCardIndex = br.ReadInt32(),
            };
        }
    }
}
