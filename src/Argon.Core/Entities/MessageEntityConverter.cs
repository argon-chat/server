namespace Argon.Entities;

using Newtonsoft.Json.Linq;
using ArgonContracts;

public class MessageEntityConverter : JsonConverter<IMessageEntity>
{
    private readonly JsonSerializerSettings _internalSettings = new()
    {
        TypeNameHandling = TypeNameHandling.All
    };

    public override void WriteJson(JsonWriter writer, IMessageEntity? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        var json = JsonConvert.SerializeObject(value, _internalSettings);
        var token = JToken.Parse(json);
        token.WriteTo(writer);
    }

    public override IMessageEntity? ReadJson(JsonReader reader, Type objectType, IMessageEntity? existingValue, 
        bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        var jObject = JObject.Load(reader);
        var typeToken = jObject["$type"];
        
        if (typeToken == null)
            throw new JsonSerializationException("Missing $type in IMessageEntity");

        var type = Type.GetType(typeToken.ToString(), throwOnError: true);
        return (IMessageEntity)JsonConvert.DeserializeObject(jObject.ToString(), type!, _internalSettings)!;
    }
}
