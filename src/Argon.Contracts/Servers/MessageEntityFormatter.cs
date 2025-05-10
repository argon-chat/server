namespace Argon;

using MessagePack.Formatters;

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
            default:
                MessagePackSerializer.Serialize(ref writer, value, options);
                break;
        }
    }

    public MessageEntity Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;

        var clone = reader;

        var mapCount = clone.ReadMapHeader();
        string? typeString = null;

        for (var i = 0; i < mapCount; i++)
        {
            var key = clone.ReadString();
            if (key == nameof(MessageEntity.Type))
            {
                typeString = clone.ReadString();
                break;
            }
            clone.Skip(); // skip value
        }

        if (typeString == null)
            throw new InvalidOperationException("MessageEntity is missing 'type' field");

        if (!Enum.TryParse<EntityType>(typeString, true, out var entityType))
            throw new InvalidOperationException($"Unknown MessageEntity type '{typeString}'");

        switch (entityType)
        {
            case EntityType.Mention:
            return MessagePackSerializer.Deserialize<MessageEntityMention>(ref reader, options);
            case EntityType.Email:
            return MessagePackSerializer.Deserialize<MessageEntityEmail>(ref reader, options);
            case EntityType.Hashtag:
            return MessagePackSerializer.Deserialize<MessageEntityHashTag>(ref reader, options);
            case EntityType.Quote:
            return MessagePackSerializer.Deserialize<MessageEntityQuote>(ref reader, options);
            case EntityType.Url:
            return MessagePackSerializer.Deserialize<MessageEntityUrl>(ref reader, options);
            default:
            return MessagePackSerializer.Deserialize<MessageEntity>(ref reader, options);
        }
    }
}