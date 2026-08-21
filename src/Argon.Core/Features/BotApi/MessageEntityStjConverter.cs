namespace Argon.Features.BotApi;

using System.Text.Json;
using System.Text.Json.Serialization;
using ArgonContracts;

/// <summary>
/// System.Text.Json converter for <see cref="IMessageEntity"/> — dispatches by the "type" field (EntityType enum).
/// Used by ASP.NET Minimal API for request deserialization (e.g. SendMessageRequest).
/// </summary>
public sealed class MessageEntityStjConverter : JsonConverter<IMessageEntity>
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert == typeof(IMessageEntity);

    public override IMessageEntity? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            throw new JsonException("IMessageEntity missing 'type' field");

        var entityType = (EntityType)typeProp.GetInt32();

        var raw = root.GetRawText();

        return entityType switch
        {
            EntityType.Bold              => JsonSerializer.Deserialize<MessageEntityBold>(raw, options),
            EntityType.Italic            => JsonSerializer.Deserialize<MessageEntityItalic>(raw, options),
            EntityType.Strikethrough     => JsonSerializer.Deserialize<MessageEntityStrikethrough>(raw, options),
            EntityType.Spoiler           => JsonSerializer.Deserialize<MessageEntitySpoiler>(raw, options),
            EntityType.Monospace         => JsonSerializer.Deserialize<MessageEntityMonospace>(raw, options),
            EntityType.Fraction          => JsonSerializer.Deserialize<MessageEntityFraction>(raw, options),
            EntityType.Ordinal           => JsonSerializer.Deserialize<MessageEntityOrdinal>(raw, options),
            EntityType.Capitalized       => JsonSerializer.Deserialize<MessageEntityCapitalized>(raw, options),
            EntityType.Mention           => JsonSerializer.Deserialize<MessageEntityMention>(raw, options),
            EntityType.MentionEveryone   => JsonSerializer.Deserialize<MessageEntityMentionEveryone>(raw, options),
            EntityType.MentionRole       => JsonSerializer.Deserialize<MessageEntityMentionRole>(raw, options),
            EntityType.Email             => JsonSerializer.Deserialize<MessageEntityEmail>(raw, options),
            EntityType.Hashtag           => JsonSerializer.Deserialize<MessageEntityHashTag>(raw, options),
            EntityType.Quote             => JsonSerializer.Deserialize<MessageEntityQuote>(raw, options),
            EntityType.Underline         => JsonSerializer.Deserialize<MessageEntityUnderline>(raw, options),
            EntityType.Url               => JsonSerializer.Deserialize<MessageEntityUrl>(raw, options),
            EntityType.SystemCallStarted => JsonSerializer.Deserialize<MessageEntitySystemCallStarted>(raw, options),
            EntityType.SystemCallEnded   => JsonSerializer.Deserialize<MessageEntitySystemCallEnded>(raw, options),
            EntityType.SystemCallTimeout => JsonSerializer.Deserialize<MessageEntitySystemCallTimeout>(raw, options),
            EntityType.SystemUserJoined  => JsonSerializer.Deserialize<MessageEntitySystemUserJoined>(raw, options),
            EntityType.Attachment        => JsonSerializer.Deserialize<MessageEntityAttachment>(raw, options),
            _ => throw new JsonException($"Unknown EntityType: {entityType}")
        };
    }

    public override void Write(Utf8JsonWriter writer, IMessageEntity value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
