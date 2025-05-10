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

        writer.WriteArrayHeader(2);

        writer.Write((ushort)value.Type);

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
                writer.WriteMapHeader(3);
                writer.Write(nameof(MessageEntity.Type));
                writer.Write((ushort)value.Type);
                writer.Write(nameof(MessageEntity.Offset));
                writer.Write(value.Offset);
                writer.Write(nameof(MessageEntity.Length));
                writer.Write(value.Length);
                writer.Write(nameof(MessageEntity.Version));
                writer.Write(value.Version);
            break;
        }
    }

    public MessageEntity? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil()) return null;

        var count = reader.ReadArrayHeader();
        if (count != 2) throw new InvalidOperationException("Invalid MessageEntity format");

        var type = (EntityType)reader.ReadUInt16();

        switch (type)
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
                var mapCount = reader.ReadMapHeader();
                var offset   = 0;
                var length   = 0;
                var version  = 0;
                for (var i = 0; i < mapCount; i++)
                {
                    var key = reader.ReadString();
                    switch (key)
                    {
                        case nameof(MessageEntity.Offset): offset = reader.ReadInt32(); break;
                        case nameof(MessageEntity.Length): length = reader.ReadInt32(); break;
                        case nameof(MessageEntity.Version): length = reader.ReadInt32(); break;
                        default:                           reader.Skip(); break;
                    }
                }
                return new MessageEntity
                {
                    Type   = type,
                    Offset = offset,
                    Length = length,
                    Version = version
                };
        }
    }
}