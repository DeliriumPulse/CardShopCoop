using System.Collections.Generic;
using CardShopCoop.Sync;
using UnityEngine;

namespace CardShopCoop.Net.Messages
{
    /// <summary>Client -> host: one box possession update.
    /// A claim is a Held/Placing update on a Free box; a release is Free; a removal
    /// is Removed. The host validates ownership from its lease map.</summary>
    [NetworkMessage(MsgType.BoxUpdate, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class BoxUpdateMessage : INetMessage
    {
        public BoxWire Box;

        /// <summary>The item count the report's Box.ItemCount was based on, so the host can
        /// apply the reporter's delta to its current authoritative count instead of the
        /// reporter's stale absolute. Ignored on host-to-client snapshots.</summary>
        public int ContentBaseItemCount;

        /// <summary>Wire id of the item type actually moved by this delta (the type removed on a
        /// take, or added on an add). Kept separate from Box.ItemType because vanilla clears a
        /// compartment's type to None when its last item is taken, which would otherwise lose
        /// the transferred identity. -1 when no item moved.</summary>
        public int ContentTransferType = -1;

        /// <summary>Non-zero when this report carries an item delta: a loose-box edit, or the
        /// current owner editing while held/placing. The host echoes it in a BoxTransferResult
        /// so the requester can reconcile exactly the transfer it sent, independent of ordering
        /// or later snapshots.</summary>
        public uint TransferSeq;

        public MsgType Type
        {
            get
            {
                return MsgType.BoxUpdate;
            }
        }
    }

    /// <summary>Host -> sender: the outcome of one loose-box item delta. AcceptedDelta is how
    /// much of the requested delta the host applied to its authoritative count, so the
    /// requester can roll back the part that could not be transferred (last to pull loses).
    /// Sent only in reply to a BoxUpdateMessage whose TransferSeq is non-zero.</summary>
    [NetworkMessage(MsgType.BoxTransferResult, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class BoxTransferResultMessage : INetMessage
    {
        public uint TransferSeq;
        public ushort BoxId;
        public int AcceptedDelta;

        public MsgType Type
        {
            get
            {
                return MsgType.BoxTransferResult;
            }
        }
    }

    /// <summary>Host -> clients: the authoritative box state (all families in one list).</summary>
    [NetworkMessage(MsgType.BoxSnapshot, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class BoxSnapshotMessage : INetMessage
    {
        public List<BoxWire> Boxes = new List<BoxWire>();
        /// <summary>True for a complete snapshot (the client may sweep absent ids). False for
        /// a partial: only the listed boxes changed and the rest are unchanged.</summary>
        public bool Full;

        public MsgType Type
        {
            get
            {
                return MsgType.BoxSnapshot;
            }
        }
    }

    /// <summary>Client -> host: the guest finished loading the borrowed world and needs
    /// authoritative state resent. Join-time snapshots are emitted during world transfer, before
    /// the guest can apply them.</summary>
    [NetworkMessage(MsgType.JoinResyncRequest, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class JoinResyncRequestMessage : INetMessage
    {
        public MsgType Type
        {
            get
            {
                return MsgType.JoinResyncRequest;
            }
        }
    }

    /// <summary>Client -> host: the local player is physically pushing this box. A transient
    /// pose/velocity stream (self-replacing, replaced by the next frame or the settle's
    /// reliable Free) so the host can mirror the push without a possession edge. The host
    /// validates that the box is loose (Free, not stored, not held) before accepting it.</summary>
    [NetworkMessage(MsgType.BoxMotion, Policy = MessagePolicy.HostOnlyInGame, Delivery = Delivery.Transient)]
    public sealed class BoxMotionMessage : INetMessage
    {
        public ushort Id;
        public Vector3 Pos;
        public float Yaw;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;

        public MsgType Type
        {
            get
            {
                return MsgType.BoxMotion;
            }
        }
    }

    /// <summary>Host -> clients: the authoritative/relayed motion of a box a player is pushing.
    /// <see cref="DriverConn"/> is the host-side conn id of the player driving the push
    /// (0 = the host player); the driver ignores its own relay and every other peer smooths
    /// the box from it. Transient: a lost frame is replaced by the next one.</summary>
    [NetworkMessage(MsgType.BoxMotionState, Policy = MessagePolicy.ClientOnlyInGame, Delivery = Delivery.Transient)]
    public sealed class BoxMotionStateMessage : INetMessage
    {
        public ushort Id;
        public int DriverConn;
        public Vector3 Pos;
        public float Yaw;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;

        public MsgType Type
        {
            get
            {
                return MsgType.BoxMotionState;
            }
        }
    }
}
