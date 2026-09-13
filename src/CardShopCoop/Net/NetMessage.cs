using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace CardShopCoop.Net
{
    /// <summary>Typed representation of one protocol message. Payload serialization is
    /// centralized in <see cref="WireCodec"/>; message DTOs contain data only.</summary>
    public interface INetMessage
    {
        MsgType Type
        {
            get;
        }
    }

    public static class NetMessageCodec
    {
        public static byte[] Encode(INetMessage message)
        {
            if (message == null)
                throw new ArgumentNullException("message");
            var payload = WireCodec.Serialize(message);
            var frame = new byte[Msg.FrameHeaderSize + Msg.TypeSize + payload.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length + Msg.TypeSize), 0, frame, 0, Msg.FrameHeaderSize);
            frame[Msg.FrameHeaderSize] = (byte)message.Type;
            Buffer.BlockCopy(payload, 0, frame, Msg.FrameHeaderSize + Msg.TypeSize, payload.Length);
            return frame;
        }
    }

    /// <summary>Single protocol payload serializer. JSON is intentionally used instead of
    /// UnityEngine.JsonUtility: Json.NET supports converters, which lets enum id translation
    /// remain exactly at the wire boundary.</summary>
    public static class WireCodec
    {
        public static byte[] Serialize(INetMessage message)
        {
            if (message == null)
                throw new ArgumentNullException("message");
            return SerializeObject(message, message.GetType());
        }

        public static byte[] SerializeObject(object value)
        {
            if (value == null)
                throw new ArgumentNullException("value");
            return SerializeObject(value, value.GetType());
        }

        private static byte[] SerializeObject(object value, Type type)
        {
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, type, WireSettings.Settings));
        }

        [ThreadStatic] private static StringWriter _lengthWriter;
        [ThreadStatic] private static JsonSerializer _lengthSerializer;
        [ThreadStatic] private static char[] _lengthChars;

        /// <summary>UTF8 payload length of a value without materializing the JSON string or
        /// byte[] (used for chunk sizing on the hot unreliable NPC stream).</summary>
        public static int SerializeUtf8Length(object value)
        {
            if (value == null)
                throw new ArgumentNullException("value");
            if (_lengthWriter == null)
            {
                _lengthWriter = new StringWriter(CultureInfo.InvariantCulture);
                _lengthSerializer = JsonSerializer.Create(WireSettings.Settings);
            }
            var sb = _lengthWriter.GetStringBuilder();
            sb.Length = 0;
            _lengthSerializer.Serialize(_lengthWriter, value, value.GetType());
            int n = sb.Length;
            if (n == 0)
                return 0;
            if (_lengthChars == null || _lengthChars.Length < n)
                _lengthChars = new char[n];
            sb.CopyTo(0, _lengthChars, 0, n);
            return Encoding.UTF8.GetByteCount(_lengthChars, 0, n);
        }

        public static INetMessage Deserialize(Type type, byte[] payload)
        {
            if (type == null)
                throw new ArgumentNullException("type");
            if (payload == null)
                throw new ArgumentNullException("payload");
            return (INetMessage)JsonConvert.DeserializeObject(Encoding.UTF8.GetString(payload), type, WireSettings.Settings);
        }
    }
}
