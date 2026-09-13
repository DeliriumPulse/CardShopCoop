namespace CardShopCoop.Sync
{
    /// <summary>
    /// The one rule for box possession ownership.
    ///
    /// Held/Placing claim or keep the lease: accepted iff the box is unowned or the sender is
    /// already the owner. Free/Removed release or retire it: accepted iff the sender is the
    /// current owner, or the host previously accepted that sender as the owner of this box
    /// (its last owner). A client that merely observes a loose box - notably a joiner's initial
    /// mirror scan - can therefore never rewrite the host's pose/content or destroy the box.
    /// Content-specific validation remains in each family adapter; lifecycle ownership does not.
    /// </summary>
    public static class BoxAuthority
    {
        public static bool AcceptBoxUpdate(PlayerRef currentOwner, PlayerRef sender,
            BoxPossession possession, PlayerRef lastOwner)
        {
            if (possession == BoxPossession.Held || possession == BoxPossession.Placing)
                return !currentOwner.IsOwned || currentOwner == sender;
            if (currentOwner.IsOwned)
                return currentOwner == sender;
            return lastOwner.IsOwned && lastOwner == sender;
        }

        /// <summary>The owner after accepting a BoxUpdate: the sender for Held/Placing,
        /// none for Free/Removed.</summary>
        public static PlayerRef NextBoxOwner(PlayerRef sender, BoxPossession possession)
        {
            return possession == BoxPossession.Held || possession == BoxPossession.Placing
                ? sender
                : PlayerRef.None;
        }
    }
}
