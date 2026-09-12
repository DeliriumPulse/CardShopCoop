using System;
using System.Collections.Generic;

namespace CardShopCoop.Net
{
    [Flags]
    public enum MessagePolicy
    {
        Any = 0, HostOnly = 1, ClientOnly = 2, InGameOnly = 4, HostOnlyInGame = 5, ClientOnlyInGame = 6
    }
    public enum Delivery
    {
        Reliable, Transient
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class NetworkMessageAttribute : Attribute
    {
        public MsgType Type
        {
            get; private set;
        }
        public Delivery Delivery
        {
            get; set;
        }
        public MessagePolicy Policy
        {
            get; set;
        }
        public NetworkMessageAttribute(MsgType type)
        {
            Type = type;
        }
    }

    public sealed class MessageContext
    {
        public int ConnectionId;
        public CoopRole Role;
        public bool InGame;
        public ICoopTransport Transport;
    }

    public sealed class MessageRouter
    {
        private sealed class Route
        {
            public MsgType Type;
            public MessagePolicy Policy;
            public Action<MessageContext, INetMessage> Handler;
            // Retry/heal metadata replaces the former per-MsgType switches in CoopCore.
            // Only the types registered here are retried; a bounded failure calls
            // Heal so the owning module re-baselines instead of replaying forever.
            public bool Retryable;
            public Action Heal;
        }
        private readonly Dictionary<Type, Route> _routes = new Dictionary<Type, Route>();
        private readonly Dictionary<MsgType, Route> _byType = new Dictionary<MsgType, Route>();

        public MessageRouter Register<T>(Action<MessageContext, T> handler,
            MessagePolicy policy = MessagePolicy.Any, bool retryable = false, Action heal = null) where T : INetMessage
        {
            if (handler == null)
                throw new ArgumentNullException("handler");
            var metadata = (NetworkMessageAttribute)Attribute.GetCustomAttribute(typeof(T), typeof(NetworkMessageAttribute));
            if (metadata == null)
                throw new InvalidOperationException(typeof(T).Name + " is missing [NetworkMessage]");
            var route = new Route
            {
                Type = metadata.Type,
                Policy = policy == MessagePolicy.Any ? metadata.Policy : policy,
                Handler = (context, message) => handler(context, (T)message),
                Retryable = retryable,
                Heal = heal
            };
            _routes[typeof(T)] = route;
            _byType[route.Type] = route;
            return this;
        }

        public bool Dispatch(MessageContext context, INetMessage message)
        {
            if (context == null || message == null)
                return false;
            Route route;
            if (!_routes.TryGetValue(message.GetType(), out route))
                return false;
            if (!Allowed(route.Policy, context))
                return false;
            route.Handler(context, message);
            return true;
        }

        public bool IsRegistered(INetMessage message)
        {
            return message != null && _routes.ContainsKey(message.GetType());
        }

        /// <summary>True when a failed dispatch of this type should be retried.</summary>
        public bool IsRetryable(MsgType type)
        {
            return _byType.TryGetValue(type, out var route) && route.Retryable;
        }

        /// <summary>Ask the owning module to re-baseline after a dropped message.</summary>
        public void Heal(MsgType type)
        {
            if (_byType.TryGetValue(type, out var route))
                route.Heal?.Invoke();
        }

        private static bool Allowed(MessagePolicy policy, MessageContext context)
        {
            if ((policy & MessagePolicy.HostOnly) != 0 && context.Role != CoopRole.Host)
                return false;
            if ((policy & MessagePolicy.ClientOnly) != 0 && context.Role != CoopRole.Client)
                return false;
            return (policy & MessagePolicy.InGameOnly) == 0 || context.InGame;
        }
    }
}
