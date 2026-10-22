using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Runtime identity for placed objects. ShelfManager re-indexes its lists whenever an
    /// object is removed, so list indexes are not identities. The host assigns a session-local
    /// ushort and the population roster binds that same id to the corresponding client object.
    /// </summary>
    internal static class PlacedObjectIdentity
    {
        public const ushort Invalid = 0;

        private sealed class ReferenceComparer : IEqualityComparer<InteractableObject>
        {
            public bool Equals(InteractableObject a, InteractableObject b) => ReferenceEquals(a, b);
            public int GetHashCode(InteractableObject value) => RuntimeHelpers.GetHashCode(value);
        }

        private static readonly Dictionary<InteractableObject, ushort> ByObject
            = new Dictionary<InteractableObject, ushort>(new ReferenceComparer());
        private static readonly Dictionary<ushort, InteractableObject> ById
            = new Dictionary<ushort, InteractableObject>();
        private static ushort _next = 1;

        public static void Reset()
        {
            ByObject.Clear();
            ById.Clear();
            _next = 1;
        }

        public static bool TryGet(InteractableObject obj, out ushort id)
        {
            if (obj != null && ByObject.TryGetValue(obj, out id) && id != Invalid)
                return true;
            id = Invalid;
            return false;
        }

        public static ushort AssignHost(InteractableObject obj)
        {
            if (obj == null)
                return Invalid;
            if (TryGet(obj, out ushort existing))
                return existing;

            ushort id;
            do
            {
                id = _next++;
                if (_next == Invalid)
                    _next = 1;
            }
            while (id == Invalid || ById.ContainsKey(id));

            Bind(obj, id);
            return id;
        }

        public static void Bind(InteractableObject obj, ushort id)
        {
            if (obj == null || id == Invalid)
                return;

            if (ByObject.TryGetValue(obj, out ushort old) && old != id)
                ById.Remove(old);
            if (ById.TryGetValue(id, out var previous) && !ReferenceEquals(previous, obj))
                ByObject.Remove(previous);

            ByObject[obj] = id;
            ById[id] = obj;
        }

        public static void Forget(InteractableObject obj)
        {
            if (obj == null)
                return;
            if (ByObject.TryGetValue(obj, out ushort id))
            {
                ByObject.Remove(obj);
                if (ById.TryGetValue(id, out var current) && ReferenceEquals(current, obj))
                    ById.Remove(id);
            }
        }

        public static bool TryMakeCompartmentKey(int kind, InteractableObject obj, int compartment,
            out int key)
        {
            key = 0;
            ushort id;
            if (CoopCore.Role == CoopRole.Host)
                id = AssignHost(obj);
            else if (!TryGet(obj, out id))
                return false;
            key = (kind << 24) | (id << 8) | (compartment & 0xFF);
            return true;
        }

        public static bool TryMakeObjectKey(int kind, InteractableObject obj, out int key)
        {
            key = 0;
            ushort id;
            if (CoopCore.Role == CoopRole.Host)
                id = AssignHost(obj);
            else if (!TryGet(obj, out id))
                return false;
            key = (kind << 24) | id;
            return true;
        }

        public static bool TryResolve(ShelfManager sm, int kind, ushort id,
            out InteractableObject result)
        {
            result = null;
            if (sm == null || id == Invalid)
                return false;
            var list = PopulationSync.GetList(sm, kind);
            if (list == null)
                return false;
            for (int i = 0; i < list.Count; i++)
            {
                var obj = list[i] as InteractableObject;
                if (obj != null && TryGet(obj, out ushort current) && current == id)
                {
                    result = obj;
                    return true;
                }
            }
            return false;
        }

        public static ushort ObjectIdFromCompartmentKey(int key)
            => (ushort)((key >> 8) & 0xFFFF);

        public static ushort ObjectIdFromObjectKey(int key)
            => (ushort)(key & 0xFFFF);
    }
}
