using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Net.Messages
{
    [NetworkMessage(MsgType.EconContrib, Policy = MessagePolicy.HostOnly)]
    public sealed class EconContributionMessage : INetMessage
    {
        public byte Kind;
        public float Value;
        public MsgType Type
        {
            get
            {
                return MsgType.EconContrib;
            }
        }
    }

    [NetworkMessage(MsgType.EconDelta, Policy = MessagePolicy.ClientOnly)]
    public sealed class EconDeltaMessage : INetMessage
    {
        // 1 = AddCoin, 2 = ReduceCoin, 3 = AddShopExp. These match the
        // contribution kinds, but the value here is the value shown by the host HUD.
        public byte Kind;
        public float Value;
        public MsgType Type
        {
            get
            {
                return MsgType.EconDelta;
            }
        }
    }

    [NetworkMessage(MsgType.MovePreview, Delivery = Delivery.Transient)]
    public sealed class MovePreviewMessage : INetMessage
    {
        // 0 = start, 1 = update, 2 = stop.
        public byte Phase;
        public int ObjectKey;
        // Host is 0; clients use their host connection id. This survives host relay.
        public int SourceId;
        public bool IsBox;
        public Vector3 Pos;
        public Quaternion Rot;
        public bool Valid;
        public MsgType Type
        {
            get
            {
                return MsgType.MovePreview;
            }
        }
    }

    [NetworkMessage(MsgType.SprayHit, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class SprayHitMessage : INetMessage
    {
        public Vector3 Position;
        public float Range;
        public int Potency;
        public MsgType Type
        {
            get
            {
                return MsgType.SprayHit;
            }
        }
    }

    [NetworkMessage(MsgType.GradedRemove)]
    public sealed class GradedRemoveMessage : INetMessage
    {
        public CardData Card;
        public MsgType Type
        {
            get
            {
                return MsgType.GradedRemove;
            }
        }
    }

    [NetworkMessage(MsgType.CardPriceSet)]
    public sealed class CardPriceSetMessage : INetMessage
    {
        public CardData Card;
        public float Price;
        public MsgType Type
        {
            get
            {
                return MsgType.CardPriceSet;
            }
        }
    }

    [NetworkMessage(MsgType.LicenseUnlock)]
    public sealed class LicenseUnlockMessage : INetMessage
    {
        public EItemType ItemType;
        public bool IsBig;
        public string RestockName;
        public MsgType Type
        {
            get
            {
                return MsgType.LicenseUnlock;
            }
        }
    }

    [NetworkMessage(MsgType.ItemPriceContrib, Policy = MessagePolicy.HostOnly)]
    public sealed class ItemPriceContribMessage : INetMessage
    {
        public EItemType ItemType;
        public float Price;
        public MsgType Type
        {
            get
            {
                return MsgType.ItemPriceContrib;
            }
        }
    }
}
