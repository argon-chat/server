namespace Argon;

using MessagePack.Formatters;
using Serilog;

public class MessageEntityResolver : IFormatterResolver
{
    public static readonly MessageEntityResolver Instance = new();

    private MessageEntityResolver()
    {
    }

    public IMessagePackFormatter<T> GetFormatter<T>()
    {
        if (typeof(T) == typeof(MessageEntity))
            return (IMessagePackFormatter<T>)new MessageEntityFormatter();
        return null;
    }
}

public class MessageEntityFormatter : IMessagePackFormatter<MessageEntity?>
{
    public void Serialize(ref MessagePackWriter writer, MessageEntity? value, MessagePackSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNil();
            return;
        }

        try
        {
            switch (value.Type)
            {
                case EntityType.Mention:
                    MessagePackSerializer.Serialize(ref writer, (MessageEntityMention)value, options);
                    break;
                case EntityType.Email:
                    MessagePackSerializer.Serialize(ref writer, (MessageEntityEmail)value, options);
                    break;
                case EntityType.Hashtag:
                    MessagePackSerializer.Serialize(ref writer, (MessageEntityHashTag)value, options);
                    break;
                case EntityType.Quote:
                    MessagePackSerializer.Serialize(ref writer, (MessageEntityQuote)value, options);
                    break;
                case EntityType.Url:
                    MessagePackSerializer.Serialize(ref writer, (MessageEntityUrl)value, options);
                    break;
                case EntityType.Underline:
                    MessagePackSerializer.Serialize(ref writer, (MessageEntityUnderline)value, options);
                    break;
                default:
                    writer.WriteMapHeader(4);
                    writer.Write(nameof(MessageEntity.Type));
                    writer.Write(value.Type.ToString());
                    writer.Write(nameof(MessageEntity.Offset));
                    writer.Write(value.Offset);
                    writer.Write(nameof(MessageEntity.Length));
                    writer.Write(value.Length);
                    writer.Write(nameof(MessageEntity.Version));
                    writer.Write(value.Version);
                    return;
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "failed to serialize MessageEntity, {valueType}, kind: {Kind}", value.GetType(), value.Type);
            throw;
        }
    }

    public MessageEntity? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;

        var clone    = reader;
        var mapCount = clone.ReadMapHeader();

        string? typeStr = null;

        for (var i = 0; i < mapCount; i++)
        {
            var key = clone.ReadString();
            if (key == "Type")
            {
                typeStr = clone.ReadString();
                break;
            }

            clone.Skip();
        }

        if (typeStr is null)
            throw new MessagePackSerializationException("Missing 'Type' field");

        if (!Enum.TryParse<EntityType>(typeStr, true, out var entityType))
            throw new MessagePackSerializationException($"Unknown EntityType: {typeStr}");

        return entityType switch
        {
            EntityType.Mention   => MessagePackSerializer.Deserialize<MessageEntityMention>(ref reader, options),
            EntityType.Email     => MessagePackSerializer.Deserialize<MessageEntityEmail>(ref reader, options),
            EntityType.Hashtag   => MessagePackSerializer.Deserialize<MessageEntityHashTag>(ref reader, options),
            EntityType.Quote     => MessagePackSerializer.Deserialize<MessageEntityQuote>(ref reader, options),
            EntityType.Url       => MessagePackSerializer.Deserialize<MessageEntityUrl>(ref reader, options),
            EntityType.Underline => MessagePackSerializer.Deserialize<MessageEntityUnderline>(ref reader, options),
            _ => DeserializeBaseEntity(ref reader)
        };
    }

    private static MessageEntity DeserializeBaseEntity(ref MessagePackReader reader)
    {
        var        count  = reader.ReadMapHeader();
        EntityType type   = default;
        int        offset = 0, length = 0, version = 0;

        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString();
            switch (key)
            {
                case nameof(MessageEntity.Type):
                    type = Enum.Parse<EntityType>(reader.ReadString()!, true);
                    break;
                case nameof(MessageEntity.Offset):
                    offset = reader.ReadInt32();
                    break;
                case nameof(MessageEntity.Length):
                    length = reader.ReadInt32();
                    break;
                case nameof(MessageEntity.Version):
                    version = reader.ReadInt32();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new MessageEntity
        {
            Type    = type,
            Offset  = offset,
            Length  = length,
            Version = version
        };
    }
}