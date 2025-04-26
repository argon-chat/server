namespace Argon.Streaming;

using System.Formats.Asn1;
using Events;
using MessagePack.Formatters;
using Orleans.Serialization;

[TsInterface]
public interface IArgonEvent
{
    [TsIgnore]
    public static string Namespace => $"argon_events";

    [JsonIgnore]
    public string EventKey { get; set; }
}

public class IArgonEvent_Resolver : IMessagePackFormatter<IArgonEvent>
{
    private static readonly Dictionary<string, Type> CacheEvents = new();

    public void Serialize(ref MessagePackWriter writer, IArgonEvent value, MessagePackSerializerOptions options)
    {
        var type = value.GetType();
        writer.WriteMapHeader(type.GetProperties().Length + 1);

        writer.Write("EventKey");
        writer.Write(value.EventKey);


        foreach (var prop in type.GetProperties())
        {
            if (!prop.CanRead || prop is { GetMethod.IsStatic: true })
                continue;
            writer.Write(prop.Name);
            var propValue = prop.GetValue(value);
            MessagePackSerializer.Serialize(prop.PropertyType, ref writer, propValue, options);
        }
    }

    public IArgonEvent Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        // Считываем заголовок
        var    mapCount   = reader.ReadMapHeader();
        string eventKey   = null;
        var    properties = new Dictionary<string, object>();

        // Проходим по всем полям карты
        for (var i = 0; i < mapCount; i++)
        {
            var key = reader.ReadString()!;
            if (key == "EventKey")
                eventKey = reader.ReadString()!;
            else
                properties[key] = ReadPropertyValue(ref reader, options);
        }

        if (string.IsNullOrEmpty(eventKey))
        {
            throw new InvalidOperationException("Missing EventKey.");
        }

        var eventType = GetEventTypeFromCache(eventKey);
        if (eventType == null)
            throw new InvalidOperationException($"No event type found for EventKey: {eventKey}");

        var eventInstance = CreateInstanceWithConstructor(eventType, properties);

        foreach (var prop in eventType.GetProperties())
            if (properties.TryGetValue(prop.Name, out var value))
                prop.SetValue(eventInstance, Convert.ChangeType(value, prop.PropertyType));

        return (IArgonEvent)eventInstance;
    }

    private object CreateInstanceWithConstructor(Type type, Dictionary<string, object> properties)
    {
        var constructor = type.GetConstructors()
           .OrderBy(c => c.GetParameters().Length)
           .FirstOrDefault();
        if (constructor == null)
            throw new InvalidOperationException($"No suitable constructor found for type {type.Name}");

        var parameters = constructor.GetParameters()
           .Select(param => properties.TryGetValue(param.Name!, out var property) ? property : null)
           .ToArray();
        return constructor.Invoke(parameters);
    }


    private object ReadPropertyValue(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var type = reader.NextMessagePackType;

        switch (type)
        {
            case MessagePackType.Map:
                return MessagePackSerializer.Deserialize<Dictionary<string, object>>(ref reader, options);
            case MessagePackType.Array:
                return MessagePackSerializer.Deserialize<object[]>(ref reader, options);
            case MessagePackType.String:
                return reader.ReadString();
            case MessagePackType.Integer:
                return reader.ReadInt32();
            case MessagePackType.Float:
                return reader.ReadSingle();
            case MessagePackType.Boolean:
                return reader.ReadBoolean();
            case MessagePackType.Nil:
                return null;
            default:
                throw new InvalidOperationException($"Unsupported MessagePack type: {type}");
        }
    }

    private Type GetEventTypeFromCache(string eventKey)
    {
        if (CacheEvents.TryGetValue(eventKey, out var cachedType))
            return cachedType;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var types = assembly.GetTypes();
            foreach (var type in types)
            {
                if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ArgonEvent<>)) 
                    continue;
                var eventKeyValue = type.Name;

                if (eventKeyValue != eventKey) continue;
                CacheEvents[eventKey] = type;
                return type;
            }
        }

        throw new CodecNotFoundException($"Not found type '{eventKey}'");
    }
}

public class ArgonEventResolver : IFormatterResolver
{
    public static readonly ArgonEventResolver Instance = new();

    private ArgonEventResolver()
    {
    }

    public IMessagePackFormatter<T> GetFormatter<T>()
    {
        if (typeof(T) == typeof(IArgonEvent))
            return (IMessagePackFormatter<T>)new IArgonEvent_Resolver();
        return null;
    }
}