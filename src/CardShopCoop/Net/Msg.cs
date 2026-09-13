using System;
using System.Collections.Concurrent;
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
        CardShelfDelta = 24,   // host -> client: authoritative card display slots
        CardShelfRequest = 25, // client -> host: joiner placed/removed a display card
        CardPriceSet = 26,     // both ways: a card's marked price changed
        RegisterState = 27,    // host -> client: observer checkout state + mannedBy
        Roster = 29,           // host -> clients: connId->name table for relayed peers
        RelayState = 30,       // host -> clients: another client's PlayerState [senderId + state]
        RelayTag = 31,         // host -> clients: another client's emote/activity [senderId + kind]
        ObjMoveDelta = 32,     // host -> client: authoritative placed-object transforms
        ObjMoveRequest = 33,   // client -> host: joiner moved a shelf/counter/decoration
        ShopName = 37,         // host -> client: the shop's name
        ItemPriceContrib = 38, // client -> host: joiner set an item price
        LightState = 39,       // host -> client: full LightTimeData (sky phase, timers)
        PopState = 40,         // host -> client: placed-object population roster (all kinds)
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
        EnumSync = 63,         // host -> client: the host's enum_values.json (card-ID registry)
        Toast = 64,            // host -> client: one-line on-screen notice
        CatalogDigest = 65,    // client -> host: restock catalog identities (mismatch diagnosis)
        GradedRemove = 68,     // both ways: a graded card left the shared album (RemoveGradedCard)
        SprayHit = 69,         // client -> host: handheld deodorant hold-spray (pos+range+potency), replay against host customers
        CardDeltaBatch = 70,   // both ways: [int count][count x CardDelta payload] - one frame for a whole frame's card changes
        // BOTH WAYS, and the same payload writer/reader serves both directions on purpose.
        // Client -> host on the catalog gating (first send in-game, then hash change, min 45s);
        // host -> client only in REPLY to a divergent one, so the guest can see its own
        // one-sided difference. Report-only: receiving it never changes a card.
        GradedDigest = 71,     // both ways: graded-cert existence digest (album + hold + shelves + boxes + in-progress)
        RegisterCart = 72,     // host -> client: authoritative cart, prices, phase, and scanned slots
        RegisterOp = 73,       // client -> host: the manning player's register action (scan / payment / change / finish)
        StaffInteract = 74,    // host -> client: worker interaction lease grant/release/denial
        ContainerBoxTake = 75, // host -> client: empty-box take result (storage index + box id, 0 = rejected)
        NpcSpeech = 77,      // host -> client: customer speech bubble
        TvState = 78,        // host -> client: shared RTCGO stream state
        TvOp = 79,           // client -> host: RTCGO stream control request
        ContainerPackClaim = 80, // host -> client: authoritative auto-opener claim result
        PurchaseRequest = 81,    // client -> host: atomic purchase request
        PlayerModelRequest = 82, // client -> host: this player's appearance
        PlayerModelState = 83,   // host -> clients: authoritative appearance roster
        PurchaseResult = 84,     // host -> requesting client: purchase outcome
        PlayerIntent = 85,       // client -> host: single-shot interaction intent
        EconDelta = 86,          // host -> clients: replay a vanilla money/XP HUD delta
        MovePreview = 87,        // peers: transient furniture placement preview
        BoxUpdate = 89,          // client -> host: one box possession update (claim/change/release/remove)
        BoxSnapshot = 90,        // host -> clients: authoritative box state list (all families)
        BoxCollect = 91,         // client -> host: open a graded card box (host mints the cards)
        BoxCollectResult = 92,   // host -> client: collect rejected (restore the mirror)
        FurnitureBoxOp = 93,     // client -> host: place / sell / box-up a furniture object
        JoinResyncRequest = 94,  // client -> host: guest finished loading; resend authoritative state
        BoxMotion = 95,        // client -> host: transient motion of a box the local player is pushing
        BoxMotionState = 96,   // host -> clients: relayed/authoritative motion of a pushed box
        NpcMoneyPopup = 97,   // host -> client: green money popup above a customer
        BoxTransferResult = 98, // host -> sender: how much of a loose-box item delta was accepted
        ShelfTransferResult = 99, // host -> sender: how much of a shelf item delta was accepted
        RegisterRejected = 100, // host -> client: a register op was rejected; reset that counter
        ShelfBoxPull = 101, // client -> host: move one shelf item directly into a held box
    }

    /// <summary>One received message, already reassembled and decoded from the wire.
    /// Transports never expose stream fragments to callers.</summary>
    public struct InMsg
    {
        public int ConnId;
        public MsgType Type;
        public INetMessage Message;
        // Local-only dispatch bookkeeping; never serialized.
        public byte DispatchAttempts;
    }

    /// <summary>Frame builders/parsers. Wire format per frame:
    /// [int32 payloadLen+1][byte MsgType][UTF-8 JSON payload].</summary>
    public static class Msg
    {
        public const int FrameHeaderSize = 4;
        public const int TypeSize = 1;
        public const int MinimumFrameSize = FrameHeaderSize + TypeSize;
        public const int MaxFrameSize = 64 * 1024 * 1024;
        // Patch releases must not change the wire revision. A new message or a
        // substantial change behind an existing message requires a minor bump.
        // 1.2.3 therefore becomes 102 (major * 100 + minor).
        public static readonly int WireVersion = DeriveWireVersion(CoopPlugin.Version);
        private const int DecodeWarningCooldownMs = 2000;
        private static readonly ConcurrentDictionary<MsgType, int> LastDecodeWarning
            = new ConcurrentDictionary<MsgType, int>();

        internal static void LogDecodeWarning(MsgType type, string warning)
        {
            int now = Environment.TickCount;
            while (true)
            {
                if (LastDecodeWarning.TryGetValue(type, out int last))
                {
                    if (unchecked(now - last) < DecodeWarningCooldownMs)
                        return;
                    if (!LastDecodeWarning.TryUpdate(type, now, last))
                        continue;
                }
                else if (!LastDecodeWarning.TryAdd(type, now))
                {
                    continue;
                }
                break;
            }
            CoopPlugin.Log?.LogWarning(warning);
        }

        private static int DeriveWireVersion(string version)
        {
            if (!Version.TryParse(version, out Version parsed)
                || parsed.Major < 0 || parsed.Minor < 0
                || parsed.Major > 999 || parsed.Minor > 999)
            {
                throw new InvalidOperationException(
                    CoopPlugin.Name + " version must have major and minor components from 0 to 999: " + version);
            }

            return parsed.Major * 100 + parsed.Minor;
        }

        /// <summary>
        /// Decodes one complete wire frame. This is the only protocol-framing entry
        /// point used by transports. TCP and Steam both hand this method a complete
        /// frame and receive the same result, so neither transport knows about the
        /// message-type byte or payload layout.
        /// </summary>
        public static bool TryDecodeFrame(byte[] frame, int offset, int count, int connId,
            int maxFrame, out InMsg message)
        {
            message = default(InMsg);
            if (frame == null || offset < 0 || count < MinimumFrameSize
                || offset > frame.Length - count)
                return false;

            int declared = BitConverter.ToInt32(frame, offset);
            if (declared < TypeSize || declared > maxFrame)
                return false;
            if (declared + FrameHeaderSize != count)
                return false;

            int payloadLength = declared - TypeSize;
            var payload = new byte[payloadLength];
            if (payloadLength > 0)
                Buffer.BlockCopy(frame, offset + FrameHeaderSize + TypeSize,
                    payload, 0, payloadLength);

            message = new InMsg
            {
                ConnId = connId,
                Type = (MsgType)frame[offset + FrameHeaderSize]
            };
            // Decode centrally. A malformed or unknown DTO is DROPPED (return false) so a
            // single bad frame can never unwind a transport pump thread - the same fail-safe
            // the transport used to get from the switch's per-message try/catch. Unknown
            // message types and malformed payloads are rate-limited per type.
            try
            {
                message.Message = MessageRegistry.Deserialize(message.Type, payload);
            }
            catch (Exception e)
            {
                MsgType type = (MsgType)frame[offset + FrameHeaderSize];
                if (MessageRegistry.IsKnown(type))
                    LogDecodeWarning(type, $"discarding malformed {type} frame: {e.GetType().Name}: {e.Message}");
                return false;
            }
            return true;
        }

        /// <summary>Reads exactly one complete frame from a stream. The stream
        /// implementation only supplies bytes; all frame-size validation and frame
        /// assembly lives here.</summary>
        public static byte[] ReadFrame(Stream stream, int maxFrame)
        {
            if (stream == null)
                throw new ArgumentNullException("stream");
            var header = new byte[FrameHeaderSize];
            ReadExact(stream, header, 0, header.Length);
            int declared = BitConverter.ToInt32(header, 0);
            if (declared < TypeSize || declared > maxFrame)
                throw new IOException("Bad frame length " + declared);

            var frame = new byte[FrameHeaderSize + declared];
            Buffer.BlockCopy(header, 0, frame, 0, header.Length);
            ReadExact(stream, frame, FrameHeaderSize, declared);
            return frame;
        }

        private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, offset + read, count - read);
                if (n <= 0)
                    throw new IOException("Connection closed");
                read += n;
            }
        }

        /// <summary>Returns the type from a complete outbound frame without making
        /// callers depend on the wire offset. Used only for transport scheduling.</summary>
        public static bool TryGetType(byte[] frame, out MsgType type)
        {
            type = default(MsgType);
            if (frame == null || frame.Length < MinimumFrameSize)
                return false;
            int declared = BitConverter.ToInt32(frame, 0);
            if (declared < TypeSize || declared + FrameHeaderSize != frame.Length)
                return false;
            type = (MsgType)frame[FrameHeaderSize];
            return true;
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

    }
}
