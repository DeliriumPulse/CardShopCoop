using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CardShopCoop.Net
{
    /// <summary>Discovers attributed DTOs once and delegates all payload decoding to WireCodec.</summary>
    public static class MessageRegistry
    {
        private static readonly Dictionary<MsgType, Type> Types = new Dictionary<MsgType, Type>();

        static MessageRegistry()
        {
            foreach (var type in LoadableTypes(typeof(MessageRegistry).Assembly))
            {
                var attribute = (NetworkMessageAttribute)Attribute.GetCustomAttribute(type, typeof(NetworkMessageAttribute));
                if (attribute == null || !typeof(INetMessage).IsAssignableFrom(type))
                    continue;
                Types[attribute.Type] = type;
            }
        }

        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                var result = new List<Type>();
                foreach (var type in e.Types)
                    if (type != null)
                        result.Add(type);
                return result;
            }
        }

        public static INetMessage Deserialize(MsgType type, byte[] payload)
        {
            Type messageType;
            if (!Types.TryGetValue(type, out messageType))
            {
                Msg.LogDecodeWarning(type,
                    "network: no DTO registered for message type " + type + " - frame dropped");
                throw new InvalidDataException("No DTO registered for " + type);
            }
            return WireCodec.Deserialize(messageType, payload ?? new byte[0]);
        }

        internal static bool IsKnown(MsgType type)
        {
            return Types.ContainsKey(type);
        }
    }
}
