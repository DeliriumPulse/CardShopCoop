using System;
using System.Collections.Generic;
using CardShopCoop.Net;

namespace CardShopCoop.Net.Messages
{
    // ---------------------------------------------------------------- handshake
    // Every field below is in WIRE ORDER, and each Serialize/Deserialize pair is the
    // single source of truth for its message's byte layout. The same shape is produced
    // and consumed by CoopCore (SendHello / SendWorldTo / the MsgType.* switch in
    // Dispatch); these DTOs are the typed mirror of those payloads.

    /// <summary>client -> host. See CoopCore.SendHello and case MsgType.Hello.</summary>
    [NetworkMessage(MsgType.Hello, Policy = MessagePolicy.HostOnly)]
    public sealed class HelloMessage : INetMessage
    {
        public int WireVersion;
        public string Version;
        public string PlayerName;
        public string Password;
        public string PluginHash;
        public string EnumHash;
        public string CardsHash;
        public List<string> PluginList = new List<string>();
        public List<string> CardsList = new List<string>();
        public byte[] EnumBlob = new byte[0];
        public string GameVersion;
        public string UnityVersion;
        public ulong SteamId;

        public MsgType Type
        {
            get
            {
                return MsgType.Hello;
            }
        }


    }

    /// <summary>host -> client. See CoopCore.SendWorldTo and case MsgType.Welcome.</summary>
    [NetworkMessage(MsgType.Welcome, Policy = MessagePolicy.ClientOnly)]
    public sealed class WelcomeMessage : INetMessage
    {
        public int WireVersion;
        public string Version;
        public string HostName;
        public int SaveLength;
        public int HostSlot;
        public int BundleLength;
        // int, not byte: the host's connection ids are unbounded; a byte wrapped at 255.
        public int SelfId;
        public byte[] HostEnumBlob = new byte[0];
        public byte[] HostCardsBlob = new byte[0];
        public ulong SteamId;

        public MsgType Type
        {
            get
            {
                return MsgType.Welcome;
            }
        }


    }

    // ---------------------------------------------------------------- world / bundle transfer
    // The chunk messages carry a raw (already-gzipped) byte slice. Offset is written and
    // read but the client currently ignores it (TCP keeps order; kept for sanity/debug).

    /// <summary>host -> client. See SendWorldTo and case MsgType.SaveChunk.</summary>
    [NetworkMessage(MsgType.SaveChunk, Policy = MessagePolicy.ClientOnly)]
    public sealed class SaveChunkMessage : INetMessage
    {
        public int Offset;
        public byte[] Data = new byte[0];

        public MsgType Type
        {
            get
            {
                return MsgType.SaveChunk;
            }
        }


    }

    /// <summary>host -> client. See SendWorldTo and case MsgType.SaveDone.</summary>
    [NetworkMessage(MsgType.SaveDone, Policy = MessagePolicy.ClientOnly)]
    public sealed class SaveDoneMessage : INetMessage
    {
        public int TotalLength;

        public MsgType Type
        {
            get
            {
                return MsgType.SaveDone;
            }
        }


    }

    /// <summary>host -> client. See SendWorldTo and case MsgType.BundleChunk.</summary>
    [NetworkMessage(MsgType.BundleChunk, Policy = MessagePolicy.ClientOnly)]
    public sealed class BundleChunkMessage : INetMessage
    {
        public int Offset;
        public byte[] Data = new byte[0];

        public MsgType Type
        {
            get
            {
                return MsgType.BundleChunk;
            }
        }


    }

    /// <summary>host -> client. See SendWorldTo and case MsgType.BundleDone.</summary>
    [NetworkMessage(MsgType.BundleDone, Policy = MessagePolicy.ClientOnly)]
    public sealed class BundleDoneMessage : INetMessage
    {
        public int TotalLength;

        public MsgType Type
        {
            get
            {
                return MsgType.BundleDone;
            }
        }


    }

    // ---------------------------------------------------------------- registry sync

    /// <summary>host -> client. See case MsgType.EnumSync (the Data is the gzipped
    /// enum_values.json; the receiver calls Msg.Gunzip on it).</summary>
    [NetworkMessage(MsgType.EnumSync, Policy = MessagePolicy.ClientOnly)]
    public sealed class EnumSyncMessage : INetMessage
    {
        public byte[] Data = new byte[0];

        public MsgType Type
        {
            get
            {
                return MsgType.EnumSync;
            }
        }


    }

    // ---------------------------------------------------------------- digests (report-only)

    /// <summary>client -> host. See CoopCore.SendCatalogDigest / CompareCatalogs (case
    /// MsgType.CatalogDigest). One entry per orderable product.</summary>
    [NetworkMessage(MsgType.CatalogDigest, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class CatalogDigestMessage : INetMessage
    {
        public List<CatalogDigestEntry> Entries = new List<CatalogDigestEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.CatalogDigest;
            }
        }


    }

    /// <summary>One catalog row. ItemType crosses the wire via Msg.WriteItemType so a
    /// client sends the host's id space; NameHash is Fnv(name), IsBigBox the tray size.</summary>
    public sealed class CatalogDigestEntry
    {
        public EItemType ItemType;
        public bool IsBigBox;
        public int NameHash;
    }

    /// <summary>both ways, report-only. See CoopCore.WriteGradedDigest / ReadGradedDigest
    /// (case MsgType.GradedDigest). One entry per graded-cert existence.</summary>
    [NetworkMessage(MsgType.GradedDigest, Policy = MessagePolicy.InGameOnly)]
    public sealed class GradedDigestMessage : INetMessage
    {
        public List<GradedDigestEntry> Entries = new List<GradedDigestEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.GradedDigest;
            }
        }


    }

    /// <summary>One graded-cert row. Expansion/Monster are translated at the wire boundary
    /// (Msg.WriteExpansion / WriteMonsterType); Border is a vanilla id space and stays raw,
    /// exactly as Msg.WriteCard leaves it. Encoded is a grade int, in no id space.</summary>
    public sealed class GradedDigestEntry
    {
        public ECardExpansionType Expansion;
        public EMonsterType Monster;
        public ECardBorderType Border;
        public bool IsFoil;
        public bool IsDestiny;
        public int Encoded;
    }

    // ---------------------------------------------------------------- wire helpers

    /// <summary>Shared bounded blob/capped-list primitives used by the system messages
    /// above.</summary>

}
