namespace Argon.Services.Ion;

using ion.runtime;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;

public class IonMaybeConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
        => objectType.IsGenericType && objectType.GetGenericTypeDefinition() == typeof(IonMaybe<>);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        var type         = value.GetType();
        var hasValueProp = type.GetProperty("HasValue")!;
        var hasValue     = (bool)hasValueProp.GetValue(value)!;

        if (!hasValue)
        {
            writer.WriteNull();
            return;
        }

        var innerValue = type.GetProperty("Value")!.GetValue(value);
        serializer.Serialize(writer, innerValue);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var innerType = objectType.GetGenericArguments()[0];
        if (reader.TokenType == JsonToken.Null)
        {
            var noneProp = objectType.GetProperty("None", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
            return noneProp.GetValue(null);
        }

        var innerValue = serializer.Deserialize(reader, innerType);
        var someMethod = objectType.GetMethod("Some", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        return someMethod.Invoke(null, [
            innerValue
        ]);
    }
}

public class IonArrayConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
        => objectType.IsGenericType && objectType.GetGenericTypeDefinition() == typeof(IonArray<>);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        var type       = value.GetType();
        var valuesProp = type.GetProperty("Values")!;
        var values     = valuesProp.GetValue(value) as IEnumerable;
        
        // Write as array without type information to avoid ReadOnlyCollection vs List mismatch
        writer.WriteStartArray();
        if (values != null)
        {
            foreach (var item in values)
            {
                serializer.Serialize(writer, item);
            }
        }
        writer.WriteEndArray();
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            var emptyProp = objectType.GetProperty("Empty", BindingFlags.Public | BindingFlags.Static)!;
            return emptyProp.GetValue(null);
        }

        var innerType = objectType.GetGenericArguments()[0];
        var listType  = typeof(List<>).MakeGenericType(innerType);

        var list = serializer.Deserialize(reader, listType);

        if (list != null) return Activator.CreateInstance(objectType, list);
        
        var emptyProp2 = objectType.GetProperty("Empty", BindingFlags.Public | BindingFlags.Static)!;
        return emptyProp2.GetValue(null);
    }
}

/// <summary>
/// Carries an Ion union through Newtonsoft as the union's own Ion encoding.
/// </summary>
/// <remarks>
/// <para>Orleans names the actual type of a union it is handed as an argument or a return value —
/// see <c>IonUnionTypeFilter</c> — but a union held <i>inside</i> an object is Newtonsoft's to write,
/// and Newtonsoft cannot build an interface back from JSON. A profile's list of worn cosmetics is
/// exactly that, and the first grain call that copied one failed with "could not create an instance
/// of type IWornCosmetic".</para>
///
/// <para><b>No type names in the data.</b> <c>MessageEntityConverter</c> writes <c>$type</c> and reads
/// it back through <c>Type.GetType</c>, which constructs whatever type a payload names. This writes the
/// bytes the Ion formatter produces and reads them with the same formatter, which knows the union's
/// cases and nothing else — and which carries any union nested inside this one, such as a frame's
/// parts inside a payload, without being told about it.</para>
/// </remarks>
public sealed class IonUnionConverter<TUnion> : JsonConverter where TUnion : class
{
    public override bool CanConvert(Type objectType) => typeof(TUnion).IsAssignableFrom(objectType);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not TUnion union)
        {
            writer.WriteNull();
            return;
        }

        var cbor = new System.Formats.Cbor.CborWriter();
        IonFormatterStorage<TUnion>.Write(cbor, union);
        writer.WriteValue(Convert.ToBase64String(cbor.Encode()));
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType is JsonToken.Null)
            return null;

        if (reader.TokenType is not JsonToken.String || reader.Value is not string encoded)
            throw new JsonSerializationException($"{typeof(TUnion).Name} is expected as its Ion encoding, and a {reader.TokenType} arrived");

        return IonFormatterStorage<TUnion>.Read(new System.Formats.Cbor.CborReader(Convert.FromBase64String(encoded)));
    }
}
