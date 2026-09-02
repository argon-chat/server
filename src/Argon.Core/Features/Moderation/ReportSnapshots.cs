namespace Argon.Features.Moderation;

/// <summary>
/// What the reported thing looked like when its case was opened.
/// </summary>
/// <remarks>
/// <para>Stored on the case as JSON, because the original will not wait: the author edits the
/// message, deletes it, renames the space, or clears the bio — and a moderator opening the case an
/// hour later would be looking at nothing. The old system stored ids only, and its one automatic
/// action overwrote the message text in place, which destroyed the evidence the case was
/// about.</para>
///
/// <para>A snapshot is taken once, by the first report. Later reports about the same thing join
/// the case; the content they saw is the content already kept.</para>
/// </remarks>
public sealed record ReportContentSnapshot(
    string          Kind,
    Guid?           AuthorId,
    string?         Text,
    string?         EntitiesJson,
    string?         Title,
    string?         Description,
    string?         AvatarFileId,
    Guid?           SpaceId,
    Guid?           ChannelId,
    DateTimeOffset? ContentCreatedAt,
    DateTimeOffset  TakenAt);

public static class ReportSnapshots
{
    // The same shape the message row itself is stored in, so an entity round-trips to the type it
    // was, and a moderator reading the raw JSON sees the concrete kinds.
    private static readonly JsonSerializerSettings EntitySettings = new()
    {
        TypeNameHandling = TypeNameHandling.All
    };

    public static string Serialize(ReportContentSnapshot snapshot)
        => JsonConvert.SerializeObject(snapshot);

    public static ReportContentSnapshot? Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonConvert.DeserializeObject<ReportContentSnapshot>(json);

    public static string? EntitiesJson(IReadOnlyCollection<IMessageEntity>? entities)
        => entities is { Count: > 0 } ? JsonConvert.SerializeObject(entities, EntitySettings) : null;
}
