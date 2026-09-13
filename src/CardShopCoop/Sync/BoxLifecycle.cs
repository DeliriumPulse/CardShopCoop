namespace CardShopCoop.Sync
{
    /// <summary>
    /// The one place that derives a box's rigidbody/collider physics from its
    /// authoritative possession state.
    ///
    /// Verified against the decompiled game (0.70.3): the game itself drives physics
    /// off on hold (<c>StartHoldBox</c>) and back on for a throw/drop
    /// (<c>ThrowBox</c>/<c>DropBox</c>); the placement reconciler in
    /// <see cref="BoxPlacement.TickVanillaPlacement"/> is the only place the mod
    /// re-derives it, and it must go through here so the rule lives in exactly one file.
    /// </summary>
    public static class BoxLifecycle
    {
        /// <summary>A box simulates physics iff it is Free. Held and Placing are
        /// kinematic; Removed has no physics.</summary>
        public static bool PhysicsEnabledFor(BoxPossession possession)
        {
            return possession == BoxPossession.Free;
        }

        /// <summary>The single physics writer: derive a box's rigidbody/collider state from
        /// its possession. Call this instead of touching SetPhysicsEnabled directly.</summary>
        public static void Apply(InteractablePackagingBox box, BoxPossession possession)
        {
            if (box == null)
                return;
            box.SetPhysicsEnabled(PhysicsEnabledFor(possession));
        }

        /// <summary>Physics writer for callers that work from placement intent rather than a
        /// possession state (the Placing reconciler).</summary>
        public static void ApplyEnabled(InteractablePackagingBox box, bool enabled)
        {
            if (box == null)
                return;
            box.SetPhysicsEnabled(enabled);
        }
    }
}
