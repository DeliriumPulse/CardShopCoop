namespace CardShopCoop.Sync
{
    public enum PlayerKind : byte
    {
        None = 0,
        Host = 1,
        Client = 2,
        Worker = 3,
    }

    public struct PlayerRef : System.IEquatable<PlayerRef>
    {
        public PlayerKind Kind;
        public int Id;

        public static PlayerRef None => new PlayerRef { Kind = PlayerKind.None, Id = 0 };

        public bool IsOwned => Kind != PlayerKind.None;

        public bool Equals(PlayerRef other)
        {
            return Kind == other.Kind && Id == other.Id;
        }

        public override bool Equals(object obj)
        {
            return obj is PlayerRef && Equals((PlayerRef)obj);
        }

        public override int GetHashCode()
        {
            return ((int)Kind * 397) ^ Id;
        }

        public static bool operator ==(PlayerRef left, PlayerRef right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(PlayerRef left, PlayerRef right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return Kind == PlayerKind.None ? "None" : $"{Kind}/{Id}";
        }
    }

    /// <summary>Single owner-to-avatar mapping. Avatar ids are transport-local: the
    /// host uses client connection ids and clients use 1000+connection for remote
    /// clients, while avatar id -1 always means the local player.</summary>
    public static class PlayerRegistry
    {
        public static PlayerRef Local
        {
            get
            {
                if (CoopCore.Role == CoopRole.Host)
                    return new PlayerRef { Kind = PlayerKind.Host, Id = 0 };
                if (CoopCore.Role == CoopRole.Client && CoopCore.LocalConnectionId >= 0)
                    return new PlayerRef { Kind = PlayerKind.Client, Id = CoopCore.LocalConnectionId };
                return PlayerRef.None;
            }
        }

        public static PlayerRef ForConnection(int connectionId)
        {
            return connectionId < 0
                ? PlayerRef.None
                : new PlayerRef { Kind = PlayerKind.Client, Id = connectionId };
        }

        /// <summary>True when the given host-side connection id identifies the local
        /// player (host connection 1 on a client, or the client's own conn id).</summary>
        public static bool IsLocalConnection(int connectionId)
        {
            if (CoopCore.Role == CoopRole.Host)
                return connectionId == 0;
            return CoopCore.LocalConnectionId >= 0 && connectionId == CoopCore.LocalConnectionId;
        }

        public static int ToAvatarId(PlayerRef owner)
        {
            if (owner.Kind == PlayerKind.Host)
                return CoopCore.Role == CoopRole.Client ? 1 : -1;
            if (owner.Kind != PlayerKind.Client)
                return -1;
            if (CoopCore.Role == CoopRole.Host)
                return owner.Id;
            if (CoopCore.Role == CoopRole.Client)
                return owner.Id == CoopCore.LocalConnectionId ? -1 : 1000 + owner.Id;
            return -1;
        }

        public static int ToAvatarId(byte ownerKind, int ownerId)
        {
            return ToAvatarId(new PlayerRef { Kind = (PlayerKind)ownerKind, Id = ownerId });
        }
    }
}
