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
