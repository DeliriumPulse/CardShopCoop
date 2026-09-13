using System;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Net.Messages
{
    [NetworkMessage(MsgType.Roster, Policy = MessagePolicy.ClientOnly)]
    public sealed class RosterMessage : INetMessage
    {
        public readonly System.Collections.Generic.List<RosterEntry> Entries = new System.Collections.Generic.List<RosterEntry>();
        public MsgType Type
        {
            get
            {
                return MsgType.Roster;
            }
        }
    }

    public sealed class RosterEntry
    {
        // int, not byte: host connection ids are unbounded and only wrap at int overflow.
        // A byte here silently collided once a long session passed 255 total connections.
        public int Id;
        public string Name;
        public ulong SteamId;
    }

    [NetworkMessage(MsgType.CoinSet, Policy = MessagePolicy.ClientOnly)]
    public sealed class CoinSetMessage : INetMessage
    {
        public double Coin;
        public float CoinFloat;
        public MsgType Type
        {
            get
            {
                return MsgType.CoinSet;
            }
        }
    }

    [NetworkMessage(MsgType.ProgressSet, Policy = MessagePolicy.ClientOnly)]
    public sealed class ProgressSetMessage : INetMessage
    {
        public int Experience, Level, Fame;
        public MsgType Type
        {
            get
            {
                return MsgType.ProgressSet;
            }
        }
    }

    [NetworkMessage(MsgType.DayTime, Policy = MessagePolicy.ClientOnly)]
    public sealed class DayTimeMessage : INetMessage
    {
        public int Day, Hour, Minute;
        public float MinuteFloat;
        public bool ShopOnceOpen;
        public MsgType Type
        {
            get
            {
                return MsgType.DayTime;
            }
        }
    }

    [NetworkMessage(MsgType.ShopName, Policy = MessagePolicy.ClientOnly)]
    public sealed class ShopNameMessage : INetMessage
    {
        public string Name;
        public MsgType Type
        {
            get
            {
                return MsgType.ShopName;
            }
        }
    }

    public abstract class EmptyMessage : INetMessage
    {
        public abstract MsgType Type
        {
            get;
        }
    }

    [NetworkMessage(MsgType.Ping)]
    public sealed class PingMessage : EmptyMessage
    {
        public override MsgType Type
        {
            get
            {
                return MsgType.Ping;
            }
        }
    }
    [NetworkMessage(MsgType.Pong)]
    public sealed class PongMessage : EmptyMessage
    {
        public override MsgType Type
        {
            get
            {
                return MsgType.Pong;
            }
        }
    }

    [NetworkMessage(MsgType.Bye)]
    public sealed class ByeMessage : INetMessage
    {
        public string Reason;
        public MsgType Type
        {
            get
            {
                return MsgType.Bye;
            }
        }
    }

    [NetworkMessage(MsgType.Emote)]
    public sealed class EmoteMessage : INetMessage
    {
        public byte Emote;
        public MsgType Type
        {
            get
            {
                return MsgType.Emote;
            }
        }
    }

    [NetworkMessage(MsgType.Activity)]
    public sealed class ActivityMessage : INetMessage
    {
        public byte Activity;
        public EItemType Pack;
        public MsgType Type
        {
            get
            {
                return MsgType.Activity;
            }
        }
    }

    [NetworkMessage(MsgType.RelayTag)]
    public sealed class RelayTagMessage : INetMessage
    {
        public int SenderId;
        public byte Kind;
        public EItemType Extra;
        public MsgType Type
        {
            get
            {
                return MsgType.RelayTag;
            }
        }
    }

    [NetworkMessage(MsgType.PlayerState, Delivery = Delivery.Transient)]
    public sealed class PlayerStateMessage : INetMessage
    {
        public Vector3 Position;
        public float Yaw;
        public Vector3 CameraPosition;
        public Quaternion CameraRotation;
        public float Speed;
        public byte Hold;
        public System.Collections.Generic.List<int> HoldTypes;
        public System.Collections.Generic.List<CardData> HoldCards;
        public MsgType Type
        {
            get
            {
                return MsgType.PlayerState;
            }
        }
    }

    [NetworkMessage(MsgType.RelayState, Delivery = Delivery.Transient)]
    public sealed class RelayStateMessage : INetMessage
    {
        public int SenderId;
        public PlayerStateMessage State = new PlayerStateMessage();
        public MsgType Type
        {
            get
            {
                return MsgType.RelayState;
            }
        }
    }

    [NetworkMessage(MsgType.PlayerModelRequest, Policy = MessagePolicy.HostOnly)]
    public sealed class PlayerModelRequestMessage : INetMessage
    {
        public bool Female;
        public int ModelIndex;
        // Json for CC_CharacterData. Kept as a string so absent/newer wardrobe fields
        // remain forward-compatible with older game builds.
        public string CustomizationJson;
        public MsgType Type
        {
            get
            {
                return MsgType.PlayerModelRequest;
            }
        }
    }

    [NetworkMessage(MsgType.PlayerModelState, Policy = MessagePolicy.ClientOnly)]
    public sealed class PlayerModelStateMessage : INetMessage
    {
        public readonly System.Collections.Generic.List<PlayerModelEntry> Entries =
            new System.Collections.Generic.List<PlayerModelEntry>();
        public MsgType Type
        {
            get
            {
                return MsgType.PlayerModelState;
            }
        }
    }

    public sealed class PlayerModelEntry
    {
        // 0 is the host; client ids are the host's canonical connection ids.
        // int, not byte, so ids past 255 cannot wrap onto an existing player.
        public int Id;
        public bool Female;
        public int ModelIndex;
        public string CustomizationJson;
    }
}
