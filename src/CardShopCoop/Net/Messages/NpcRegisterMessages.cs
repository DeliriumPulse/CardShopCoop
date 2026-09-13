using CardShopCoop.Net;
using CardShopCoop.Sync;
using UnityEngine;

namespace CardShopCoop.Net.Messages
{
    // ---- NpcState -------------------------------------------------------------
    // host -> client (unreliable / transient). One instance per chunk produced by
    // NpcSync.HostCollect: [float hostTime][byte count][count x NpcEntry].
    // Mirrors NpcSync.WriteEntry / NpcSync.ApplyBatch exactly.

    [NetworkMessage(MsgType.NpcState, Policy = MessagePolicy.ClientOnly, Delivery = Delivery.Transient)]
    public sealed class NpcStateMessage : INetMessage
    {
        public float HostTime;
        public System.Collections.Generic.List<NpcEntry> Entries = new System.Collections.Generic.List<NpcEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.NpcState;
            }
        }


    }

    // Host -> client (reliable / one-shot). The identity binds the bubble to the
    // current pooled customer incarnation, rather than a recycled list slot.
    [NetworkMessage(MsgType.NpcSpeech, Policy = MessagePolicy.ClientOnly)]
    public sealed class NpcSpeechMessage : INetMessage
    {
        public byte Kind;
        public ushort Index;
        public int Identity;
        public string Text;
        public float OffsetUp;

        public MsgType Type
        {
            get
            {
                return MsgType.NpcSpeech;
            }
        }


    }

    [NetworkMessage(MsgType.NpcMoneyPopup, Policy = MessagePolicy.ClientOnly)]
    public sealed class NpcMoneyPopupMessage : INetMessage
    {
        public ushort Index;
        public int Identity;
        public float Amount;
        public float OffsetUp;

        public MsgType Type
        {
            get
            {
                return MsgType.NpcMoneyPopup;
            }
        }


    }

    /// <summary>One NPC snapshot within an NpcState batch. Flags is the raw wire byte
    /// (the game's NpcFlags is a private nested enum in NpcSync).</summary>
    public sealed class NpcEntry
    {
        public byte Kind;
        public ushort Index;
        public int Identity;
        public bool HasName;
        public string CharName;   // only present when HasName
        public Vector3 Position;
        public float Yaw;
        public float Speed;
        public byte Flags;
        public int ActionSequence;
        public byte ActionKind;
        public bool HoldBig;
        public int HoldItemType;
    }

    // ---- RegisterState --------------------------------------------------------
    // host -> client (reliable). Observer checkout state: which player mans each
    // counter. Mirrors RegisterSync.WriteStates / ClientApplyState:
    // [byte count][count x (byte index, byte manned)].

    [NetworkMessage(MsgType.RegisterState, Policy = MessagePolicy.ClientOnly)]
    public sealed class RegisterStateMessage : INetMessage
    {
        public System.Collections.Generic.List<RegisterStateEntry> Entries = new System.Collections.Generic.List<RegisterStateEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.RegisterState;
            }
        }


    }

    /// <summary>0 = unmanned, 1 = host player, 2 = another guest.</summary>
    public sealed class RegisterStateEntry
    {
        public byte Index;
        public byte Manned;
        public int OwnerConnId;
    }

    // ---- RegisterCart ---------------------------------------------------------
    // host -> client (reliable). Authoritative bag + prices + phase + scanned slots
    // for every counter that currently has an active customer. Mirrors
    // RegisterSync.WriteCarts / ClientApplyCart:
    // [byte count][count x entry]
    //   entry = (byte index, int customerId) then, when customerId != 0:
    //     (ushort customerIndex, int customerGeneration, string characterName,
    //      byte state, bool isCard, double paidAmount, double totalScanned,
    //      float customerTotalScanned,
    //      double currentMoneyChange, bool changeReady, bool changeStarted, bool tooMuchChange,
    //      byte itemCount, (EItemType, float price) x itemCount, bool scanned x itemCount,
    //      byte cardCount, (CardData, float price) x cardCount, bool scanned x cardCount).

    [NetworkMessage(MsgType.RegisterCart, Policy = MessagePolicy.ClientOnly)]
    public sealed class RegisterCartMessage : INetMessage
    {
        public System.Collections.Generic.List<RegisterCartEntry> Entries = new System.Collections.Generic.List<RegisterCartEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.RegisterCart;
            }
        }


    }

    /// <summary>One counter's authoritative cart digest. CustomerId is the opaque
    /// change token (0 = customer left; no other fields are present).</summary>
    public sealed class RegisterCartEntry
    {
        public byte Index;
        public int CustomerId;
        public ushort CustomerIndex;
        public int CustomerGeneration;
        public string CharacterName;
        public byte State;
        public bool IsCard;
        public double PaidAmount;
        public double TotalScanned;
        public float CustomerTotalScanned;
        // Live checkout change state, so a client that takes over (or reconstructs) mid-change
        // sees the money already on the table and computes the same readiness as the host.
        public double CurrentMoneyChange;
        public bool ChangeReady;
        public bool ChangeStarted;
        public bool TooMuchChange;
        public System.Collections.Generic.List<EItemType> ItemTypes = new System.Collections.Generic.List<EItemType>();
        public System.Collections.Generic.List<float> ItemPrices = new System.Collections.Generic.List<float>();
        public System.Collections.Generic.List<bool> ItemScanned = new System.Collections.Generic.List<bool>();
        public System.Collections.Generic.List<CardData> Cards = new System.Collections.Generic.List<CardData>();
        public System.Collections.Generic.List<float> CardPrices = new System.Collections.Generic.List<float>();
        public System.Collections.Generic.List<bool> CardScanned = new System.Collections.Generic.List<bool>();
    }

    // host -> client (reliable). The host could not apply the manning player's op because
    // its authoritative register state disagrees; the client must drop its optimistic local
    // state for that counter and rebuild from the next authoritative cart.
    [NetworkMessage(MsgType.RegisterRejected, Policy = MessagePolicy.ClientOnly)]
    public sealed class RegisterRejectedMessage : INetMessage
    {
        public byte Index;

        public MsgType Type
        {
            get
            {
                return MsgType.RegisterRejected;
            }
        }
    }

    // ---- RegisterOp -----------------------------------------------------------
    // Both ways (reliable), reusing one DTO.
    //  - client -> host: the manning player's register action; replayed on the host through
    //    vanilla public methods (RegisterSync.HostApplyOp).
    //  - host -> clients: an applied OpGiveChange, replayed visually so every client sees the
    //    money on the table (RegisterSync.ClientApplyChange). The cart's CurrentMoneyChange/
    //    ChangeReady scalars are the catch-up/ordering backstop.
    // Mirrors the client-side emitters: [byte index][byte op][op-specific payload].

    [NetworkMessage(MsgType.RegisterOp, Policy = MessagePolicy.Any)]
    public sealed class RegisterOpMessage : INetMessage
    {
        public byte Index;
        public byte Op;
        public byte BagIndex;      // OpScanItem / OpScanCard
        public bool IsCard;        // OpTakingPayment / OpTookPayment
        public double PaidAmount;  // OpTakingPayment (host's own payment is authoritative)
        public int ChangeIndex;    // OpGiveChange
        public double ChangeValue; // OpGiveChange
        public bool TakingBack;    // OpGiveChange
        public double TotalAmount; // OpFinishCard (EvaluateCreditCard total)

        public MsgType Type
        {
            get
            {
                return MsgType.RegisterOp;
            }
        }


    }
}
