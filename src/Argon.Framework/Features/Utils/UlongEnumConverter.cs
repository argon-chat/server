namespace Argon.Api.Features.Utils;

public class UlongEnumConverter<T> : JsonConverter where T : struct, Enum
{
    public override bool CanConvert(Type objectType)
        => objectType == typeof(T);

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Integer)
            switch (reader.Value)
            {
                case long l:
                    return (T)Enum.ToObject(typeof(T), unchecked((ulong)l));
                case System.Numerics.BigInteger bi:
                    return (T)Enum.ToObject(typeof(T), (ulong)bi);
                case ulong ul:
                    return (T)Enum.ToObject(typeof(T), ul);
            }

        throw new JsonSerializationException($"Cannot convert {reader.Value} to {typeof(T).Name}");
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        var raw = Convert.ToUInt64(value);
        writer.WriteValue(raw);
    }
}