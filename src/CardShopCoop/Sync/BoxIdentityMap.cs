using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Stable identity bookkeeping shared by the box engine and its families.
    ///
    /// Each box family maps its own object instances to the same host-assigned id space,
    /// but all need the same lifetime invariant: an object/id association is created once,
    /// can be resolved in either direction, and is removed as a pair. Keeping that
    /// invariant here avoids the implementations drifting in their identity-map lifecycle
    /// while leaving their type-specific state machines independent.
    /// </summary>
    internal sealed class BoxIdentityMap<T> where T : class
    {
        private readonly Dictionary<T, ushort> _idOf = new Dictionary<T, ushort>();
        private readonly Dictionary<ushort, T> _byId = new Dictionary<ushort, T>();
        private ushort _nextId = 1;

        public Dictionary<T, ushort> IdOf
        {
            get
            {
                return _idOf;
            }
        }
        public Dictionary<ushort, T> ById
        {
            get
            {
                return _byId;
            }
        }

        public ushort GetOrAssign(T value)
        {
            if (value == null)
                return 0;
            if (_idOf.TryGetValue(value, out var existing))
                return existing;

            ushort id;
            do
            {
                id = _nextId++;
                if (_nextId == 0)
                    _nextId = 1;
            }
            while (id == 0 || _byId.ContainsKey(id));

            _idOf[value] = id;
            _byId[id] = value;
            return id;
        }

        public bool TryGetId(T value, out ushort id)
        {
            if (value == null)
            {
                id = 0;
                return false;
            }
            return _idOf.TryGetValue(value, out id);
        }

        public bool TryGetValue(ushort id, out T value)
        {
            return _byId.TryGetValue(id, out value);
        }

        public void Remove(T value)
        {
            if (value == null || !_idOf.TryGetValue(value, out var id))
                return;
            _idOf.Remove(value);
            _byId.Remove(id);
        }

        public void Remove(ushort id)
        {
            if (!_byId.TryGetValue(id, out var value))
                return;
            _byId.Remove(id);
            if (value != null)
                _idOf.Remove(value);
        }

        public void Clear()
        {
            _idOf.Clear();
            _byId.Clear();
            _nextId = 1;
        }
    }
}
