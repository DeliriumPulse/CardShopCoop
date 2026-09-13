using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Sync;
using UnityEngine;

namespace CardShopCoop.Net.Messages
{
    /// <summary>One client purchase request. The host resolves identity, recomputes the
    /// authoritative price, charges the shared wallet, and delivers the lines atomically.</summary>
    [NetworkMessage(MsgType.PurchaseRequest, Policy = MessagePolicy.HostOnly)]
    public sealed class PurchaseRequestMessage : INetMessage
    {
        // 0 = restock, 1 = furniture, 2 = license.
        public byte Kind;
        public List<PurchaseLine> Lines = new List<PurchaseLine>();
        public MsgType Type
        {
            get
            {
                return MsgType.PurchaseRequest;
            }
        }
    }

    /// <summary>Host -> the client that requested a purchase. The result is addressed by
    /// the transport connection, so a successful purchase only clears that client's UI cart.</summary>
    [NetworkMessage(MsgType.PurchaseResult, Policy = MessagePolicy.ClientOnly)]
    public sealed class PurchaseResultMessage : INetMessage
    {
        public byte Kind;
        public bool Success;
        public string Text = "";
        public MsgType Type
        {
            get
            {
                return MsgType.PurchaseResult;
            }
        }
    }

    public sealed class PurchaseLine
    {
        public int ItemType;
        public bool IsBig;
        public string Name = "";
        public int Count;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
    }

    // ------------------------------------------------------------------ Market

    /// <summary>Host -> clients: the one shared market (item/card percent changes,
    /// generated bases, game-event price rows). Field order is the exact byte order
    /// written by MarketSync.WriteState / read by MarketSync.ClientApplyState.</summary>
    [NetworkMessage(MsgType.MarketState, Policy = MessagePolicy.ClientOnly)]
    public sealed class MarketStateMessage : INetMessage
    {
        public int RollGen;
        public List<MarketPercentEntry> ItemPricePercentChangeList = new List<MarketPercentEntry>();
        public List<MarketCardEntry> GenCardMarketPriceList = new List<MarketCardEntry>();
        public List<MarketCardEntry> GenCardMarketPriceListDestiny = new List<MarketCardEntry>();
        public List<MarketCardEntry> GenCardMarketPriceListGhost = new List<MarketCardEntry>();
        public List<MarketCardEntry> GenCardMarketPriceListGhostBlack = new List<MarketCardEntry>();
        public List<MarketCardEntry> GenCardMarketPriceListMegabot = new List<MarketCardEntry>();
        public List<MarketCardEntry> GenCardMarketPriceListFantasyRPG = new List<MarketCardEntry>();
        public List<MarketCardEntry> GenCardMarketPriceListCatJob = new List<MarketCardEntry>();
        // Card market multiplier table (GetMarketPrice reads it for GRADED cards). Generated
        // locally with Unity Random on the client by RestockManager.Init, so it must be
        // overwritten by the host's copy or graded binder prices diverge.
        public List<float> GenGradedCardPriceMultiplierList = new List<float>();
        // Modded-expansion card market changes (see MarketModdedCardEntry).
        public List<MarketModdedCardEntry> ModdedCards = new List<MarketModdedCardEntry>();
        public List<float> SetGameEventPriceList = new List<float>();
        public List<float> GeneratedGameEventPriceList = new List<float>();
        public List<float> GameEventPricePercentChangeList = new List<float>();
        public List<MarketSparseEntry> GeneratedMarketPriceList = new List<MarketSparseEntry>();
        public List<MarketSparseEntry> GeneratedCostPriceList = new List<MarketSparseEntry>();
        public List<MarketSparseEntry> AverageItemCostList = new List<MarketSparseEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.MarketState;
            }
        }
    }

    /// <summary>One non-zero item price-percent-change row (index is an EItemType).</summary>
    public sealed class MarketPercentEntry
    {
        public EItemType ItemType;
        public short Percent; // already x100, clamped to [short.MinValue, short.MaxValue]
    }

    /// <summary>One per-expansion card market row: short percent (x100) + full float base.</summary>
    public sealed class MarketCardEntry
    {
        public short Percent;
        public float GeneratedMarketPrice;
    }

    /// <summary>One modded-expansion card market change. The host hooks the
    /// game's AddCardPricePercentChange / SetCardGeneratedMarketPrice (which EPL prefixes,
    /// not replaces) and the client replays the same calls, so EPL's own prefix stores it
    /// locally - no EPL types or save-data access here.</summary>
    public sealed class MarketModdedCardEntry
    {
        public ECardExpansionType Expansion;
        public int Index;
        public bool IsDestiny;
        public bool HasPercent;
        public float Percent;   // absolute pricePercentChangeList; client converts to a delta
        public float Base;      // absolute generated base, applied only when HasBase
        public bool HasBase;
    }

    /// <summary>One non-zero sparse base-price row (index is an EItemType, raw wire id space).</summary>
    public sealed class MarketSparseEntry
    {
        public EItemType ItemType;
        public float Value;
    }

    // ------------------------------------------------------------------ Report

    /// <summary>Host -> clients: the end-of-day report + customer review tail.
    /// Layout mirrors ReportSync.WriteState / ClientApplyState exactly.</summary>
    [NetworkMessage(MsgType.ReportState, Policy = MessagePolicy.ClientOnly)]
    public sealed class ReportStateMessage : INetMessage
    {
        public bool OpenScreen;
        public int CustomerVisited;
        public int CheckoutCount;
        public int CustomerDisatisfied;
        public int CustomerBoughtItem;
        public int CustomerBoughtCard;
        public int CustomerPlayed;
        public int StoreExpGained;
        public int StoreLevelGained;
        public int ItemAmountSold;
        public int CardAmountSold;
        public float TotalPlayTableTime;
        public float TotalItemEarning;
        public float TotalCardEarning;
        public float TotalPlayTableEarning;
        public float SupplyCost;
        public float UpgradeCost;
        public float EmployeeCost;
        public float RentCost;
        public float BillCost;
        public int CardPackOpened;
        public int SmellyCustomerCleaned;
        public int ManualCheckoutCount;
        public int GemMintCardObtained;
        public int ReviewCount;        // lifetime m_CustomerReviewCount (sequence number)
        public float ReviewScoreAverage;
        public List<ReportReviewEntry> Reviews = new List<ReportReviewEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.ReportState;
            }
        }


    }

    /// <summary>One customer-review row of the report tail.</summary>
    public sealed class ReportReviewEntry
    {
        public int CustomerReviewType;
        public byte StarLevel;
        public byte TextSOGoodBadLevel;
        public int TextSOIndex;
        public int Day;
        public byte Hour;
        public byte Minute;
        public EItemType ItemType;
        public string CustomerName;
    }

    // ------------------------------------------------------------- Tournament

    /// <summary>Host -> clients: tournament schedule/fee/signups + prize catalog +
    /// per-customer bracket digest. Layout mirrors TournamentSync.WriteState /
    /// ClientApplyState.</summary>
    [NetworkMessage(MsgType.TournamentState, Policy = MessagePolicy.ClientOnly)]
    public sealed class TournamentStateMessage : INetMessage
    {
        public byte Flags; // bit0 IsHostingTournament, bit1 IsTournamentDay, bit2 IsTournamentDayOver
        public int MaxPlayerCount;
        public int SignedUpCustomerCount;
        public int FinishedCurrentRoundCustomerCount;
        public int CurrentRound;
        public int MaxRound;
        public float Fee;
        public float TotalValue;
        public List<TournamentPrizeSlot> PrizeSlots = new List<TournamentPrizeSlot>();
        public List<TournamentBracketEntry> Bracket = new List<TournamentBracketEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.TournamentState;
            }
        }


    }

    /// <summary>One prize-catalog slot (a set of prize entries).</summary>
    public sealed class TournamentPrizeSlot
    {
        public List<TournamentPrizeEntry> Prizes = new List<TournamentPrizeEntry>();
    }

    /// <summary>One tournament prize (optional card, item type + count).</summary>
    public sealed class TournamentPrizeEntry
    {
        public bool HasCard;
        public CardData Card;
        public EItemType ItemType;
        public int Count;
    }

    /// <summary>One live tournament bracket entry (a signed-up competitor digest).</summary>
    public sealed class TournamentBracketEntry
    {
        public byte SortedIndex;
        public int ModelIndex;
        public byte Flags; // bit0 IsFemale, bit1 IsWin, bit2 HasResult
        public int WinCount;
        public int WinPoints;
        public int OMW;
        public int OOMW;
    }

    // --------------------------------------------------------------- PlayTable

    /// <summary>Host -> clients: on-table visual digest for customer matches.
    /// Layout mirrors PlayTableSync.WriteState / ClientApplyState.</summary>
    [NetworkMessage(MsgType.TableState, Policy = MessagePolicy.ClientOnly)]
    public sealed class TableStateMessage : INetMessage
    {
        public List<TableEntry> Tables = new List<TableEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.TableState;
            }
        }


    }

    /// <summary>One play table's digest (index + per-seat states).</summary>
    public sealed class TableEntry
    {
        public byte Index;
        public bool Occupied;
        public List<TableSeatEntry> Seats = new List<TableSeatEntry>();
    }

    /// <summary>One seat on a play table (active flag + the three set-piece EItemTypes).</summary>
    public sealed class TableSeatEntry
    {
        public bool Active;
        public EItemType PlayMat;
        public EItemType DeckBox;
        public EItemType Comic;
    }

    /// <summary>Client -> host: a single-shot interaction against shared game state.
    /// Domains own the meaning of Kind/Action; the host always resolves and validates the
    /// target before invoking the registered handler.</summary>
    [NetworkMessage(MsgType.PlayerIntent, Policy = MessagePolicy.HostOnly)]
    public sealed class PlayerIntentMessage : INetMessage
    {
        public byte Kind;
        public byte Action;
        public byte Target;
        public int ObjectKey;
        public float FloatA;
        public int IntA;
        public string Json;

        public MsgType Type
        {
            get
            {
                return MsgType.PlayerIntent;
            }
        }
    }

    // ----------------------------------------------------------------- Grading

    /// <summary>Client -> host: the joiner submitted cards for grading.
    /// Layout mirrors GradingSync.ClientSubmit / HostApplyOp.</summary>
    [NetworkMessage(MsgType.GradingOp, Policy = MessagePolicy.HostOnly)]
    public sealed class GradingOpMessage : INetMessage
    {
        public byte ServiceLevel;
        public List<CardData> Cards = new List<CardData>();
        public float Total;   // client's on-screen bill; host recomputes/validates
        public byte CompanyId; // Grading Overhaul company id (255 = no GO)

        public MsgType Type
        {
            get
            {
                return MsgType.GradingOp;
            }
        }


    }

    /// <summary>Host -> clients: the authoritative pending-grading-submission list.
    /// Layout mirrors GradingSync.WriteState / ClientApplyState.</summary>
    [NetworkMessage(MsgType.GradingState, Policy = MessagePolicy.ClientOnly)]
    public sealed class GradingStateMessage : INetMessage
    {
        public List<GradingSetEntry> Sets = new List<GradingSetEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.GradingState;
            }
        }


    }

    /// <summary>One in-progress grading submission set.</summary>
    public sealed class GradingSetEntry
    {
        public int ServiceLevel; // Grading Overhaul encoded level is the full int
        public byte DayPassed;
        public float MinutePassed;
        public List<CardData> Cards = new List<CardData>();
    }

    // -------------------------------------------------------------------- Trade

    /// <summary>Client -> host: the joiner answered a counter offer (accept carries the
    /// price the screen had set; decline/heartbeat carry 0). Layout mirrors
    /// TradeServe.SendOpFor / HostApplyOp.</summary>
    [NetworkMessage(MsgType.TradeOp, Policy = MessagePolicy.HostOnly)]
    public sealed class TradeOpMessage : INetMessage
    {
        public byte Op;           // 1 accept, 2 decline, 3 screen heartbeat
        public byte CounterIdx;
        public float Price;

        public MsgType Type
        {
            get
            {
                return MsgType.TradeOp;
            }
        }


    }

    /// <summary>Host -> clients: live trade/sell-in offers at every counter.
    /// Layout mirrors TradeServe.WriteState / ClientApplyState.</summary>
    [NetworkMessage(MsgType.TradeState, Policy = MessagePolicy.ClientOnly)]
    public sealed class TradeStateMessage : INetMessage
    {
        public bool HostBusy;
        public byte ResultSeq;
        public string Result = "";
        /// <summary>Counter the latest result belongs to (255 = none). The guest uses this to
        /// drive its open CustomerTradeCardScreen to the host-confirmed outcome.</summary>
        public byte ResultCounter = 255;
        /// <summary>TradeServe.Outcome*: what the host decided for ResultCounter.</summary>
        public byte ResultOutcome;
        public List<TradeOfferEntry> Offers = new List<TradeOfferEntry>();

        public MsgType Type
        {
            get
            {
                return MsgType.TradeState;
            }
        }


    }

    /// <summary>One live counter offer (card-for-card trade or sell-in).</summary>
    public sealed class TradeOfferEntry
    {
        public byte CounterIdx;
        public bool Known;
        public bool Trading;
        public CardData CardL;
        public CardData CardR;
        public float Price;
        public float PriceSet;
        public float LastPriceSet;
        public int MaxDeclineCount;
        public int DeclineCount;
        public float Remaining;
        public ushort CustomerIndex;
        public int CustomerGeneration;
        public Vector3 Position;
        public float Yaw;
    }

}
