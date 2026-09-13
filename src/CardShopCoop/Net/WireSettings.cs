using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace CardShopCoop.Net
{
    public static class WireSettings
    {
        public static readonly JsonSerializerSettings Settings = Create();

        private static JsonSerializerSettings Create()
        {
            var settings = new JsonSerializerSettings
            {
                Culture = CultureInfo.InvariantCulture,
                Formatting = Formatting.None,
                // Preserve constructor-initialised collection defaults when an untrusted peer
                // explicitly sends null. Nullable scalars are handled by their DTO defaults.
                NullValueHandling = NullValueHandling.Ignore,
                ContractResolver = new WireContractResolver(),
                MissingMemberHandling = MissingMemberHandling.Error,
                MaxDepth = 128,
            };
            settings.Converters.Add(new EnumWireConverter<EItemType>(Util.EnumKind.ItemType));
            settings.Converters.Add(new EnumWireConverter<EObjectType>(Util.EnumKind.ObjectType));
            settings.Converters.Add(new EnumWireConverter<EDecoObject>(Util.EnumKind.DecoObject));
            settings.Converters.Add(new EnumWireConverter<ECardExpansionType>(Util.EnumKind.CardExpansion));
            settings.Converters.Add(new EnumWireConverter<EMonsterType>(Util.EnumKind.MonsterType));
            settings.Converters.Add(new Vector3Converter());
            settings.Converters.Add(new QuaternionConverter());
            settings.Converters.Add(new WorldEntryConverter());
            settings.Converters.Add(new ObjMoveEntryConverter());
            settings.Converters.Add(new PopulationStateConverter());
            settings.Converters.Add(new PlayerStateConverter());
            return settings;
        }
    }

    internal sealed class WireContractResolver : DefaultContractResolver
    {
        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
        {
            var properties = base.CreateProperties(type, MemberSerialization.OptOut);
            for (int i = properties.Count - 1; i >= 0; i--)
            {
                var p = properties[i];
                if (p.PropertyName == "Type" || p.Ignored || (p.DeclaringType != null && p.DeclaringType.Namespace == "UnityEngine"))
                    properties.RemoveAt(i);
            }
            return properties;
        }
    }

    internal sealed class EnumWireConverter<T> : JsonConverter where T : struct
    {
        private readonly Util.EnumKind _kind;
        public EnumWireConverter(Util.EnumKind kind)
        {
            _kind = kind;
        }
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(T);
        }
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(Util.EnumMap.ToWire(_kind, Convert.ToInt32(value, CultureInfo.InvariantCulture)));
        }
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                throw new JsonSerializationException("Null enum " + objectType);
            return (T)Enum.ToObject(typeof(T), Util.EnumMap.FromWire(_kind, Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture)));
        }
    }

    internal sealed class Vector3Converter : JsonConverter
    {
        public override bool CanConvert(Type t)
        {
            return t == typeof(Vector3);
        }
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            var v = (Vector3)value;
            w.WriteStartArray();
            w.WriteValue(v.x);
            w.WriteValue(v.y);
            w.WriteValue(v.z);
            w.WriteEndArray();
        }
        public override object ReadJson(JsonReader r, Type t, object existing, JsonSerializer s)
        {
            var a = ReadArray(r, 3);
            return new Vector3(Convert.ToSingle(a[0], CultureInfo.InvariantCulture), Convert.ToSingle(a[1], CultureInfo.InvariantCulture), Convert.ToSingle(a[2], CultureInfo.InvariantCulture));
        }
        private static List<object> ReadArray(JsonReader r, int count)
        {
            var a = new List<object>();
            if (r.TokenType != JsonToken.StartArray)
                throw new JsonSerializationException("Expected vector array");
            while (r.Read() && r.TokenType != JsonToken.EndArray)
            {
                if (a.Count >= count)
                    throw new JsonSerializationException("Too many vector components");
                a.Add(r.Value);
            }
            if (a.Count != count)
                throw new JsonSerializationException("Wrong vector component count");
            return a;
        }
    }

    internal sealed class QuaternionConverter : JsonConverter
    {
        public override bool CanConvert(Type t)
        {
            return t == typeof(Quaternion);
        }
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            var q = (Quaternion)value;
            w.WriteStartArray();
            w.WriteValue(q.x);
            w.WriteValue(q.y);
            w.WriteValue(q.z);
            w.WriteValue(q.w);
            w.WriteEndArray();
        }
        public override object ReadJson(JsonReader r, Type t, object existing, JsonSerializer s)
        {
            var a = new List<object>();
            if (r.TokenType != JsonToken.StartArray)
                throw new JsonSerializationException("Expected quaternion array");
            while (r.Read() && r.TokenType != JsonToken.EndArray)
            {
                if (a.Count >= 4)
                    throw new JsonSerializationException("Too many quaternion components");
                a.Add(r.Value);
            }
            if (a.Count != 4)
                throw new JsonSerializationException("Wrong quaternion component count");
            return new Quaternion(Convert.ToSingle(a[0], CultureInfo.InvariantCulture), Convert.ToSingle(a[1], CultureInfo.InvariantCulture), Convert.ToSingle(a[2], CultureInfo.InvariantCulture), Convert.ToSingle(a[3], CultureInfo.InvariantCulture));
        }
    }
}
