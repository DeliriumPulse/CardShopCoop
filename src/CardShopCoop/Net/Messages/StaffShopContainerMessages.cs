using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Sync;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Net.Messages
{
    // ----------------------------------------------------------------------
    // Staff (worker) sync
    // ----------------------------------------------------------------------

    // StaffOp (client -> host): sub-op byte selects the payload shape.
    [NetworkMessage(MsgType.StaffOp, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class StaffOpMessage : INetMessage
    {
        private const byte OpHire = 1;
        private const byte OpUpdate = 2;
        private const byte OpBonus = 3;
        private const byte OpFire = 4;
        private const byte OpBeginInteract = 5;
        private const byte OpEndInteract = 6;

        public byte Op;
        public int Index;   // OpHire / OpBonus / OpFire / OpEndInteract / OpBeginInteract / OpUpdate
        public Vector3 Position; // OpBeginInteract
        public byte PrimaryTask;
        public byte SecondaryTask;
        public byte WorkerTask;
        public bool FillNoLabel;
        public bool RoundUpPrice;
        public bool AvoidSetCardPrice;
        public bool RoundUpCardPrice;
        public bool AvoidSetCardPriceRestock;
        public float PriceMult;
        public float CardPriceMult;
        public List<bool> PackTypes;

        public MsgType Type
        {
            get
            {
                return MsgType.StaffOp;
            }
        }


    }

    // StaffState (host -> client): [byte count][count x StaffEntry].
    [NetworkMessage(MsgType.StaffState, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class StaffStateMessage : INetMessage
    {
        public List<StaffEntry> Entries = new List<StaffEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.StaffState;
            }
        }



        private static byte PackFlags(StaffEntry e)
        {
            return (byte)((e.Hired ? 1 : 0)
                | (e.HasData ? 2 : 0)
                | (e.BonusBoosted ? 4 : 0)
                | (e.FillNoLabel ? 8 : 0)
                | (e.RoundUpPrice ? 16 : 0)
                | (e.RoundUpCardPrice ? 32 : 0)
                | (e.AvoidSetCardPrice ? 64 : 0)
                | (e.AvoidSetCardPriceRestock ? 128 : 0));
        }
    }

    public struct StaffEntry
    {
        public bool Hired;
        public bool HasData;
        public byte PrimaryTask;
        public byte SecondaryTask;
        public byte WorkerTask;
        public byte CurrentState;
        public bool GoingHome;
        public byte BonusCount;
        public bool BonusBoosted;
        public bool FillNoLabel;
        public bool RoundUpPrice;
        public bool RoundUpCardPrice;
        public bool AvoidSetCardPrice;
        public bool AvoidSetCardPriceRestock;
        public float PriceMult;
        public float CardPriceMult;
        public List<bool> PackTypes;
        public List<int> ExpList;
    }

    // StaffInteract (host -> client): worker interaction lease grant/release/denial.
    [NetworkMessage(MsgType.StaffInteract, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class StaffInteractMessage : INetMessage
    {
        public int Index;
        public bool Granted;
        public bool Occupied;

        public MsgType Type
        {
            get
            {
                return MsgType.StaffInteract;
            }
        }
    }

    // ----------------------------------------------------------------------
    // Shop state sync
    // ----------------------------------------------------------------------

    // ShopOp (client -> host): always exactly [byte op][byte arg].
    [NetworkMessage(MsgType.ShopOp, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class ShopOpMessage : INetMessage
    {
        public byte Op;
        public byte Arg;

        public MsgType Type
        {
            get
            {
                return MsgType.ShopOp;
            }
        }
    }

    // ShopState (host -> client): bills + room unlocks + sign states + tutorial snapshot.
    [NetworkMessage(MsgType.ShopState, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class ShopStateMessage : INetMessage
    {
        public ShopBillEntry Rent = new ShopBillEntry();
        public ShopBillEntry Electric = new ShopBillEntry();
        public ShopBillEntry Employee = new ShopBillEntry();
        public int UnlockRoomCount;
        public int UnlockWarehouseRoomCount;
        public bool IsWarehouseRoomUnlocked;
        public bool IsShopOpen;
        public bool IsWarehouseDoorClosed;
        public int TutorialIndex;
        public List<ShopTutorialEntry> Tutorials = new List<ShopTutorialEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.ShopState;
            }
        }


    }

    public struct ShopBillEntry
    {
        public int DayPassed;
        public float AmountToPay;
    }

    public struct ShopTutorialEntry
    {
        public int Condition;
        public float Value;
    }

    // ----------------------------------------------------------------------
    // Settings sync
    // ----------------------------------------------------------------------

    // SettingsOp (client -> host): first byte is the sub-op.
    [NetworkMessage(MsgType.SettingsOp, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class SettingsOpMessage : INetMessage
    {
        private const byte OpBuyDeco = 1;
        private const byte OpEquipDeco = 2;
        private const byte OpGameEvent = 3;
        private const byte OpGameEventFee = 4;
        private const byte OpCashier = 5;
        private const byte OpTableNumber = 6;
        private const byte OpBuyItemDeco = 7;
        private const byte OpPlaceItemDeco = 8;
        private const byte OpRemoveItemDeco = 9;

        public byte Op;
        public byte Category;    // OpBuyDeco: 0 wall / 1 floor / 2 ceiling
        public int Index;        // OpBuyDeco / OpTableNumber / OpGameEventFee (fmt) / OpGameEvent (fmt)
        public int Wall, WallB, Floor, FloorB, Ceiling, CeilingB; // OpEquipDeco
        public ECardExpansionType Expansion;  // OpGameEvent
        public float Fee;        // OpGameEventFee
        public byte CashierIndex; // OpCashier
        public byte CashierFlags; // OpCashier
        public byte TableIndex;   // OpTableNumber
        public int TableNumber;   // OpTableNumber
        public EDecoObject DecoType;
        public int ObjectKey;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;

        public MsgType Type
        {
            get
            {
                return MsgType.SettingsOp;
            }
        }


    }

    // SettingsState (host -> client): authoritative shop settings snapshot.
    [NetworkMessage(MsgType.SettingsState, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class SettingsStateMessage : INetMessage
    {
        public List<bool> WallUnlocked = new List<bool>();
        public List<bool> FloorUnlocked = new List<bool>();
        public List<bool> CeilingUnlocked = new List<bool>();
        public int EquippedWallIndex, EquippedWallIndexB;
        public int EquippedFloorIndex, EquippedFloorIndexB;
        public int EquippedCeilingIndex, EquippedCeilingIndexB;
        public int GameEventFormat;
        public int PendingGameEventFormat;
        public ECardExpansionType GameEventExpansion;
        public ECardExpansionType PendingGameEventExpansion;
        public List<float> GameEventPrices = new List<float>();
        public List<byte> CashierFlags = new List<byte>();
        public List<byte> TableNumbers = new List<byte>();
        public List<DecoStockEntry> DecoStock = new List<DecoStockEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.SettingsState;
            }
        }
    }

    // ----------------------------------------------------------------------
    // Container sync
    // ----------------------------------------------------------------------

    // ContainerOp (client -> host): first byte is the sub-op.
    [NetworkMessage(MsgType.ContainerOp, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class ContainerOpMessage : INetMessage
    {
        private const byte OpContentSet = 1;
        private const byte OpPackInsert = 2;
        private const byte OpPackTurnOn = 3;
        private const byte OpPackCollect = 4;
        private const byte OpPackClaim = 6;
        private const byte OpBoxTake = 5;
        private const byte OpCleanserToggle = 7;
        private const byte OpCleanserRefill = 8;
        private const byte OpWorkerTakeFlag = 9;
        private const byte OpBoxStoreAtomic = 10;

        public byte Op;
        public byte Kind;        // OpContentSet
        public byte Index;       // OpContentSet / OpWorkerTakeFlag / OpPackInsert / OpPackTurnOn / OpPackCollect / OpCleanserToggle / OpCleanserRefill
        public bool CanWorkerTake;   // OpContentSet / OpWorkerTakeFlag
        public List<CompactCardDataAmount> Cards = new List<CompactCardDataAmount>(); // OpContentSet / OpPackCollect
        public EItemType ItemType;   // OpPackInsert / OpBoxStoreAtomic
        public ushort StorageId;     // OpBoxTake / OpBoxStoreAtomic
        public Vector3 Position;     // OpBoxTake
        public ushort BoxId;         // OpBoxStoreAtomic
        public bool IsBig;           // OpBoxStoreAtomic
        public bool TurnedOn;        // OpCleanserToggle
        public float Fill;           // OpCleanserRefill
        public int ClaimToken;       // OpPackCollect

        public MsgType Type
        {
            get
            {
                return MsgType.ContainerOp;
            }
        }


    }

    // ContainerState (host -> client): [ushort record count][records, keyed by kind].
    [NetworkMessage(MsgType.ContainerState, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class ContainerStateMessage : INetMessage
    {
        public List<ContainerRecord> Records = new List<ContainerRecord>();

        public MsgType Type
        {
            get
            {
                return MsgType.ContainerState;
            }
        }
    }

    public struct ContainerRecord
    {
        public byte Kind;
        public byte Index;
        public bool CanWorkerTake;
        public List<CompactCardDataAmount> Cards;
        public List<EItemType> StoredTypes;
        public bool Processing;
        public float Timer;
        public int OpenedCount;
        public ushort StorageId;
        public int Count;
        public byte Flags;
        public List<float> Fills;
        public int CurrentState;
        public bool CollectClaimed;
    }

    // ContainerBoxTake (host -> client): empty-box take acceptance / rejection.
    [NetworkMessage(MsgType.ContainerBoxTake, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class ContainerBoxTakeMessage : INetMessage
    {
        public ushort StorageId;
        public ushort BoxId;
        public int Remaining;

        public MsgType Type
        {
            get
            {
                return MsgType.ContainerBoxTake;
            }
        }
    }

    [NetworkMessage(MsgType.ContainerPackClaim, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class ContainerPackClaimMessage : INetMessage
    {
        public byte Index;
        public bool Accepted;
        public int ClaimToken;
        public List<CompactCardDataAmount> Cards = new List<CompactCardDataAmount>();

        public MsgType Type
        {
            get
            {
                return MsgType.ContainerPackClaim;
            }
        }
    }

    public sealed class DecoStockEntry
    {
        public int DecoType;
        public int Count;
    }

    // ----------------------------------------------------------------------
    // Shared compact-card-list wire codec (matches ContainerSync.WriteCards/ReadCards).
    // ----------------------------------------------------------------------


}
