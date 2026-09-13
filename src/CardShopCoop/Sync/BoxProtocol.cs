using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The entire possession protocol. A box is Free, Held, Placing, or Removed -
    /// nothing else is synced. Visibility, physics, label, owner, pose and velocity
    /// are all derived from this.
    /// </summary>
    public enum BoxPossession : byte
    {
        Free = 0,     // loose, thrown, dropping, stored; host owns pose/velocity
        Held = 1,     // in the owning player's hand; hidden from everyone else
        Placing = 2,  // owner is placing it (Q drag or opened furniture); follows owner camera
        Removed = 3,  // sell / trash / unpack / despawn
    }

    public enum BoxFamily : byte
    {
        Item = 0,
        Card = 1,
        Furniture = 2,
    }

    /// <summary>One box on the wire. Content is family-tagged (only the fields for the
    /// entry's Family are meaningful). This replaces every per-family box channel.</summary>
    public struct BoxWire
    {
        public ushort Id;
        public BoxFamily Family;
        public BoxPossession Possession;

        // Only meaningful for Held/Placing on the host->client leg. The host fills it in
        // for relays; clients leave it 0 (their own identity is implicit from the link).
        public int OwnerConn;

        // Only meaningful for Free.
        public Vector3 Pos;
        public float Yaw;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;

        // Free + stored.
        public ushort StoreShelfId;
        public byte StoreShelf;
        public byte StoreComp;
        // Shelf and compartment indices are zero-based, so (0,0,0) is a valid address.
        public bool Stored;

        // Orthogonal flags.
        public bool Open;
        public bool Big;
        public bool Unmapped;
        // Furniture only: the boxed object is being placed (R open) rather than the delivery
        // box itself (Q drag). Receivers hide the box instead of following it.
        public bool Unpack;

        // Item content.
        public int ItemType;
        public int ItemCount;

        // Card content.
        public List<CardData> Cards;

        // Furniture content (the boxed object's identity).
        public int ObjType;
        public int ObjIndex;
        public int NameHash;
        public byte Kind;

        public bool IsStored => Stored;
    }
}
