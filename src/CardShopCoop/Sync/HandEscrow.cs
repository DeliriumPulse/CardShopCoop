using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Client-only reservation area for optimistic item mutations. Reserved items stay
    /// live in the hand until the host says whether the mutation really happened. This is
    /// deliberately token-keyed: box and world acknowledgements have independent sequences.</summary>
    internal static class HandEscrow
    {
        private static readonly Dictionary<int, List<Item>> ReservedTakes
            = new Dictionary<int, List<Item>>();
        private static readonly Dictionary<int, List<Item>> RetainedRejected
            = new Dictionary<int, List<Item>>();
        private static readonly HashSet<Item> Reserved = new HashSet<Item>();
        private static readonly List<Item> ToHand = new List<Item>();
        private static readonly Dictionary<Item, RecentTake> Recent = new Dictionary<Item, RecentTake>();
        private static int _nextToken;
        private static bool _resetting;
        private static int _suppressNoteDepth;
        private struct RecentTake
        {
            public int Type; public float At;
        }
        internal static bool IsNoteSuppressed => _suppressNoteDepth > 0;

        public static void BeginSuppressNote() => _suppressNoteDepth++;
        public static void EndSuppressNote()
        {
            if (_suppressNoteDepth > 0)
                _suppressNoteDepth--;
        }

        public static void NoteTakenItem(Item item)
        {
            if (CoopCore.Role != CoopRole.Client || item == null || IsNoteSuppressed)
                return;
            Recent[item] = new RecentTake { Type = (int)item.GetItemType(), At = Time.realtimeSinceStartup };
            Reserved.Add(item);
        }

        public static bool IsInLocalHand(Item item)
        {
            if (item == null)
                return false;
            var held = CoopCore.GetHeldItemList(CoopCore.PlayerIpc);
            // Conservative: if the hand list cannot be read (scene-load window), treat a reserved
            // item as held so its protection is not lifted.
            return held == null || held.Contains(item);
        }

        public static int ReserveTake(int localType, int count, out int effectiveType)
        {
            effectiveType = localType;
            if (CoopCore.Role != CoopRole.Client)
                return 0;
            if (count <= 0)
                return 0;
            PruneRecentTaken();
            var items = new List<Item>(count);
            var noted = new List<Item>();
            var held = CoopCore.GetHeldItemList(CoopCore.PlayerIpc);
            if (held != null)
            {
                int notedType = int.MinValue;
                bool hasLocalType = false;
                bool mixed = false;
                for (int i = held.Count - 1; i >= 0 && items.Count < count; i--)
                    if (held[i] != null && Recent.ContainsKey(held[i])
                        )
                    {
                        int type = Recent[held[i]].Type;
                        if (type == localType)
                            hasLocalType = true;
                        else if (notedType == int.MinValue)
                            notedType = type;
                        else if (notedType != type)
                            mixed = true;
                    }
                if (hasLocalType)
                    effectiveType = localType;
                else if (notedType != int.MinValue && !mixed)
                    effectiveType = notedType;
                else if (notedType != int.MinValue && mixed)
                {
                    CoopPlugin.Log.LogWarning($"HandEscrow.ReserveTake: noted items have mixed types for local type {localType}; reserved 0");
                    return 0;
                }

                // Bind one effective type only: just-noted items first, then ordinary hand items.
                for (int i = held.Count - 1; i >= 0 && items.Count < count; i--)
                    if (held[i] != null && Recent.ContainsKey(held[i])
                        && Recent[held[i]].Type == effectiveType)
                    {
                        items.Add(held[i]);
                        noted.Add(held[i]);
                    }
                for (int i = held.Count - 1; i >= 0 && items.Count < count; i--)
                    if (held[i] != null && !Reserved.Contains(held[i])
                        && !items.Contains(held[i])
                        && (int)held[i].GetItemType() == effectiveType)
                        items.Add(held[i]);
            }
            if (items.Count != count)
            {
                CoopPlugin.Log.LogWarning($"HandEscrow.ReserveTake: wanted {count} of type {effectiveType}, found {items.Count}; reserved 0");
                return 0;
            }

            int token = NextToken();
            for (int i = 0; i < noted.Count; i++)
                Recent.Remove(noted[i]);
            for (int i = 0; i < items.Count; i++)
                Reserved.Add(items[i]);
            ReservedTakes.Add(token, items);
            return token;
        }

        /// <summary>Fail-closed cleanup for a take whose reservation failed: remove the
        /// just-noted item(s) from the hand so host truth restoring the container cannot leave a
        /// duplicate. Matches the expected type first, then any noted take.</summary>
        public static int RollbackUnreservedTake(int localType, int count)
        {
            if (count <= 0)
                return 0;
            var held = CoopCore.GetHeldItemList(CoopCore.PlayerIpc);
            if (held == null)
                return 0;
            var drop = new List<Item>(count);
            for (int i = held.Count - 1; i >= 0 && drop.Count < count; i--)
                if (held[i] != null && Recent.ContainsKey(held[i])
                    && Recent[held[i]].Type == localType)
                    drop.Add(held[i]);
            for (int i = held.Count - 1; i >= 0 && drop.Count < count; i--)
                if (held[i] != null && Recent.ContainsKey(held[i]) && !drop.Contains(held[i]))
                    drop.Add(held[i]);
            int rolledBack = 0;
            for (int i = 0; i < drop.Count; i++)
            {
                var item = drop[i];
                Recent.Remove(item);
                Reserved.Remove(item);
                if (CoopCore.RemoveHeldItemFromHand(item))
                {
                    CoopCore.DestroyDetachedItem(item);
                    rolledBack++;
                }
                else
                {
                    Reserved.Add(item);
                    int deferredToken = NextToken();
                    RetainedRejected[deferredToken] = new List<Item> { item };
                    CoopPlugin.Log.LogError($"HandEscrow: deferred local cleanup armed for rollback item token {deferredToken}");
                    CoopPlugin.Log.LogError($"HandEscrow.RollbackUnreservedTake: failed to remove reserved item for type {localType}; keeping it reserved and requesting resync");
                    CoopCore.Instance?.World?.RequestResync?.Invoke();
                }
            }
            if (rolledBack != count)
                CoopPlugin.Log.LogWarning($"HandEscrow.RollbackUnreservedTake: rolled back {rolledBack} of {count} type {localType}");
            return rolledBack;
        }

        public static bool ResolveTake(int token, int acceptedMagnitude)
        {
            if (token <= 0)
                return true; // nothing was escrowed for this transfer; there is nothing to reconcile
            if (!ReservedTakes.TryGetValue(token, out var items))
            {
                CoopPlugin.Log.LogWarning($"HandEscrow.ResolveTake: unknown token {token}");
                return false;
            }
            if (acceptedMagnitude < 0)
            {
                CoopPlugin.Log.LogError($"HandEscrow.ResolveTake: negative accepted count {acceptedMagnitude} for token {token}; treating as zero");
                acceptedMagnitude = 0;
            }
            if (acceptedMagnitude > items.Count)
            {
                CoopPlugin.Log.LogWarning($"HandEscrow.ResolveTake: host accepted {acceptedMagnitude}, but token {token} contains {items.Count}");
                acceptedMagnitude = items.Count;
            }
            bool success = true;
            int failedAt = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (i < acceptedMagnitude)
                {
                    Reserved.Remove(items[i]);
                }
                else
                {
                    var item = items[i];
                    Recent.Remove(item);
                    Reserved.Remove(item);
                    if (CoopCore.RemoveHeldItemFromHand(item))
                        CoopCore.DestroyDetachedItem(item);
                    else
                    {
                        Reserved.Add(item);
                        CoopPlugin.Log.LogError(
                            $"HandEscrow.ResolveTake: rejected item could not be removed from the hand for token {token}; kept reserved and requesting resync");
                        CoopCore.Instance?.World?.RequestResync?.Invoke();
                        success = false;
                        failedAt = i;
                        break;
                    }
                }
            }
            if (success)
                ReservedTakes.Remove(token);
            else
            {
                RetainedRejected[token] = items.GetRange(failedAt, items.Count - failedAt);
                ReservedTakes.Remove(token);
                CoopPlugin.Log.LogError($"HandEscrow: deferred local cleanup armed for failed take token {token}");
            }
            return success;
        }

        /// <summary>Move the rejected tail of a take into local-only cleanup after a result
        /// handler fault. Accepted items remain in the hand; rejected items stay protected until
        /// Tick can remove them. This does not perform any network operation.</summary>
        public static void DeferRejectedTake(int token, int acceptedMagnitude)
        {
            if (!ReservedTakes.TryGetValue(token, out var items))
                return;
            acceptedMagnitude = Mathf.Clamp(acceptedMagnitude, 0, items.Count);
            for (int i = 0; i < acceptedMagnitude; i++)
                Reserved.Remove(items[i]);
            RetainedRejected[token] = items.GetRange(acceptedMagnitude, items.Count - acceptedMagnitude);
            ReservedTakes.Remove(token);
            CoopPlugin.Log.LogError($"HandEscrow: deferred local cleanup armed for failed take token {token}");
        }

        public static void ExpireTake(int token)
        {
            if (token <= 0)
                return; // nothing was escrowed for this transfer
            if (!ReservedTakes.TryGetValue(token, out var items))
            {
                if (!RetainedRejected.TryGetValue(token, out items))
                    return;
                RetainedRejected.Remove(token);
            }
            else
                ReservedTakes.Remove(token);
            for (int i = 0; i < items.Count; i++)
                Reserved.Remove(items[i]);
        }

        public static bool IsReserved(Item item) => item != null && (Reserved.Contains(item) || Recent.ContainsKey(item));

        public static bool HasReservedHeld(InteractionPlayerController ipc)
        {
            var items = CoopCore.GetHeldItemList(ipc);
            if (items == null)
                return false;
            for (int i = 0; i < items.Count; i++)
                if (IsReserved(items[i]))
                    return true;
            return false;
        }

        public static bool IsFrontReserved(InteractionPlayerController ipc)
        {
            var items = CoopCore.GetHeldItemList(ipc);
            return ipc != null && items != null
                && items.Count > 0 && IsReserved(items[0]);
        }

        public static int EscrowAdded(ShelfCompartment comp, int localType, int count)
        {
            if (comp == null || count <= 0 || (int)comp.GetItemType() != localType)
                return 0;
            int queued = 0;
            BeginSuppressNote();
            try
            {
                while (queued < count && comp.GetItemCount() > 0)
                {
                    // A network result handler is a systemic boundary: a game-side take fault here
                    // must not abort the handler with the remaining items left in the compartment.
                    // Stop, leave the rest, and let the caller rebase its baseline to the truth.
                    Item item;
                    try
                    {
                        item = comp.TakeItemToHand();
                    }
                    catch (System.Exception e)
                    {
                        CoopPlugin.Log.LogWarning($"HandEscrow.EscrowAdded: TakeItemToHand failed after {queued} of {count}: {e.Message}");
                        break;
                    }
                    if (item == null)
                    {
                        CoopPlugin.Log.LogError($"HandEscrow.EscrowAdded: compartment returned null after removing item {queued + 1}");
                        break;
                    }
                    QueueToHand(item);
                    queued++;
                }
            }
            finally { EndSuppressNote(); }
            if (queued < count)
                CoopPlugin.Log.LogWarning($"HandEscrow.EscrowAdded: wanted {count} of type {localType}, queued {queued}");
            return queued;
        }

        public static void Tick()
        {
            PruneRecentTaken();
            const int cleanupBudget = 8;
            int cleaned = 0;
            if (RetainedRejected.Count > 0)
            {
                var tokens = new List<int>(RetainedRejected.Keys);
                for (int t = 0; t < tokens.Count && cleaned < cleanupBudget; t++)
                {
                    int token = tokens[t];
                    if (!RetainedRejected.TryGetValue(token, out var items))
                        continue;
                    for (int i = items.Count - 1; i >= 0 && cleaned < cleanupBudget; i--)
                    {
                        cleaned++;
                        var item = items[i];
                        if (item == null)
                        {
                            Reserved.Remove(item);
                            items.RemoveAt(i);
                            continue;
                        }
                        if (!IsInLocalHand(item))
                        {
                            Reserved.Remove(item);
                            items.RemoveAt(i);
                            CoopPlugin.Log.LogError($"HandEscrow.Tick: deferred rejected item token {token} left the hand; releasing local guard and requesting resync");
                            CoopCore.Instance?.World?.RequestResyncCoalesced();
                            continue;
                        }
                        Reserved.Remove(item);
                        if (CoopCore.RemoveHeldItemFromHand(item))
                        {
                            CoopCore.DestroyDetachedItem(item);
                            items.RemoveAt(i);
                        }
                        else
                            Reserved.Add(item);
                    }
                    if (items.Count == 0)
                        RetainedRejected.Remove(token);
                }
            }
            if (ToHand.Count == 0 || !CoopCore.HandAcceptsItems())
                return;
            while (ToHand.Count > 0 && CoopCore.HandAcceptsItems())
            {
                var item = ToHand[0];
                if (item == null || item.gameObject == null)
                {
                    CoopPlugin.Log.LogWarning("HandEscrow.Tick: discarded a destroyed queued item");
                    ToHand.RemoveAt(0);
                    continue;
                }
                // TryAttachHeldItem activates the object only once it can actually enter the
                // hand, so a failed attach leaves it inactive instead of visibly floating.
                if (!CoopCore.TryAttachHeldItem(item))
                    break;
                ToHand.RemoveAt(0);
            }
        }

        public static void Reset()
        {
            if (_resetting)
                return;
            _resetting = true;
            try
            {
                var reservedItems = new List<Item>(Reserved);
                for (int i = 0; i < reservedItems.Count; i++)
                    Reserved.Remove(reservedItems[i]);
                ReservedTakes.Clear();
                RetainedRejected.Clear();
                Reserved.Clear();
                Tick();
                int remainder = 0;
                for (int i = 0; i < ToHand.Count; i++)
                {
                    var item = ToHand[i];
                    var ipc = CoopCore.PlayerIpc;
                    if (item != null && ipc != null && ipc.GetHoldItemCount() < HandProtection.HandCapacity)
                    {
                        item.gameObject.SetActive(true);
                        ipc.AddHoldItemToFront(item);
                    }
                    else
                    {
                        CoopCore.DestroyDetachedItem(item);
                        remainder++;
                    }
                }
                ToHand.Clear();
                Recent.Clear();
                if (remainder > 0)
                    CoopPlugin.Log.LogError($"HandEscrow.Reset: destroyed {remainder} queued items that could not return to hand");
            }
            finally
            {
                _resetting = false;
                _suppressNoteDepth = 0; // no Begin may leak a positive depth into the next session
            }
        }

        public static void PruneRecentTaken()
        {
            if (Recent.Count == 0)
                return;
            float now = Time.realtimeSinceStartup;
            var drop = new List<Item>();
            var expired = new List<RecentTake>();
            foreach (var pair in Recent)
            {
                var item = pair.Key;
                if (item == null)
                    drop.Add(item);
                else if (now - pair.Value.At > 10f)
                {
                    drop.Add(item);
                    expired.Add(pair.Value);
                }
                else if (!IsInLocalHand(item) && now - pair.Value.At > 2f)
                    drop.Add(item);
            }
            // Collect first, mutate second: pruning must not run inside the enumeration.
            for (int i = 0; i < expired.Count; i++)
            {
                // A note that reaches expiry was never consumed by ReserveTake, so no transfer
                // was ever created for it (ReserveTake removes the notes it tokenizes). Destroying
                // the hand item here could delete an item whose add+take cancelled inside one
                // scan window, so keep the item and merely reconcile to authoritative state.
                CoopPlugin.Log.LogWarning(
                    $"HandEscrow.PruneRecentTaken: unreported take note expired for type {expired[i].Type}; keeping the item and requesting resync");
                CoopCore.Instance?.World?.RequestResync?.Invoke();
            }
            for (int i = 0; i < drop.Count; i++)
            {
                Recent.Remove(drop[i]);
                Reserved.Remove(drop[i]);
            }
        }

        private static void QueueToHand(Item item)
        {
            if (item == null)
                return;
            item.gameObject.SetActive(false);
            ToHand.Add(item);
        }

        private static int NextToken()
        {
            if (++_nextToken <= 0)
                _nextToken = 1;
            while (ReservedTakes.ContainsKey(_nextToken) || RetainedRejected.ContainsKey(_nextToken))
            {
                if (++_nextToken <= 0)
                    _nextToken = 1;
            }
            return _nextToken;
        }
    }
}
