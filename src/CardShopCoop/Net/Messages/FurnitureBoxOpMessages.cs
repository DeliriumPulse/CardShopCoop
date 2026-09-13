using UnityEngine;

namespace CardShopCoop.Net.Messages
{
    /// <summary>Client -> host: a furniture OBJECT action that is not box possession -
    /// unpack-place, sell, or box-up. Possession itself rides BoxUpdate/BoxSnapshot.</summary>
    [NetworkMessage(MsgType.FurnitureBoxOp, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class FurnitureBoxOpMessage : INetMessage
    {
        public const byte OpPlace = 0;
        public const byte OpSell = 1;
        public const byte OpBoxUp = 2;
        public const byte OpRemoved = 3;

        public byte Op;
        public ushort Id;
        public byte Kind;
        public int ObjIndex;
        public int WireType;
        public int NameHash;
        public Vector3 Position;
        public float Yaw;
        public Quaternion Rotation = Quaternion.identity;
        public bool IsVertical;
        public bool IsWarehouseWall;
        public int VerticalSnapWallIndex = -1;

        public MsgType Type => MsgType.FurnitureBoxOp;
    }
}
