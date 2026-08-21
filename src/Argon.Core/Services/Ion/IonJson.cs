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