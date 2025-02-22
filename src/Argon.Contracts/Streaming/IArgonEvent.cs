namespace Argon.Streaming;

using System.Formats.Asn1;
using MessagePack.Formatters;

[TsInterface]
public interface IArgonEvent
{
    [TsIgnore]
    public static string ProviderId => "argon_events";
    [TsIgnore]
    public static string Namespace => $"@";
    [TsIgnore]
    public static string Broadcast => $"argon.cluster.events.broadcast";

    public string EventKey { get; init; }
    public long?  Sequence { get; set; }
    public int?   EventId  { get; set; }
}

public class IArgonEvent_Resolver : IMessagePackFormatter<IArgonEvent>
{
    private static readonly Dictionary<string, Dictionary<string, Type>> CacheEvents = new();

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
        => throw new NotImplementedException();
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