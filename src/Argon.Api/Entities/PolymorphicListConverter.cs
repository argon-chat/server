namespace Argon.Entities;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class PolymorphicListConverter<TBase> : JsonConverter<List<TBase>>
{
    private readonly JsonSerializerSettings InternalSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Auto
    };

    public override void WriteJson(JsonWriter writer, List<TBase>? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartArray();
        foreach (var token in value
                    .Select(item => JsonConvert
                        .SerializeObject(item, InternalSettings))
                    .Select(JToken.Parse))
            token.WriteTo(writer);

        writer.WriteEndArray();
    }

    public override List<TBase>? ReadJson(JsonReader reader, Type objectType, List<TBase>? existingValue, bool hasExistingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        var array  = JArray.Load(reader);
        var result = new List<TBase>(array.Count);

        foreach (var item in array)
        {
            var typeToken = item["$type"];
            if (typeToken == null)
                throw new JsonSerializationException("Missing $type in polymorphic item.");

            var type = Type.GetType(typeToken.ToString()!, throwOnError: true);
            var obj  = (TBase)JsonConvert.DeserializeObject(item.ToString(), type!, InternalSettings)!;
            result.Add(obj);
        }

        return result;
    }
}