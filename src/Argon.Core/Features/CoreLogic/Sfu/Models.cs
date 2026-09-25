namespace Argon.Sfu;

using System.Diagnostics.CodeAnalysis;

public record ArgonUserId([field: Id(0)] Guid id)
{
    public string ToRawIdentity() => id.ToString();

    public static implicit operator ArgonUserId(Guid userId) => new(userId);

    public bool IsGuest
    {
        get
        {
            Span<byte> rawId = stackalloc byte[16];
            id.TryWriteBytes(rawId);
            return rawId[..4] == [0xFF, 0xFF, 0xFF, 0xFF];
        }
    }
}

public record ArgonRoomId([field: Id(0)] Guid PrefixId, [field: Id(1)] Guid ShardId)
{
    public string ToRawRoomId() => $"{PrefixId}/{ShardId}";

    public bool IsNotLinkedMeetId()
    {
        Span<byte> rawId = stackalloc byte[16];
        PrefixId.TryWriteBytes(rawId);
        return rawId[..4] == [0xFF, 0xFF, 0xFF, 0xFF];
    }

    public static ArgonRoomId FromArgonChannel(Guid SpaceId, Guid ChannelId) => new ArgonRoomId(SpaceId, ChannelId);
    public static ArgonRoomId FromMeetId(string meetId)
    {
        var splied = meetId.Split('/');
        var first  = splied.First();
        var second = splied.Last();
        if (!Guid.TryParse(first, out var prefixId))
            throw new FormatException($"PrefixId is not valid prefix");
        if (!Guid.TryParse(second, out var shardId))
            throw new FormatException($"ShardId is not valid shard");
        return new ArgonRoomId(prefixId, shardId);
    }
}
/// <summary>The radio room of a broadcast channel: <c>radio/{space}/{channel}</c>. Not a channel room, so the webhook never reads it as one.</summary>
public record RadioRoomId([field: Id(0)] Guid SpaceId, [field: Id(1)] Guid ChannelId)
{
    public const string Prefix = "radio/";

    public string ToRawRoomId() => $"{Prefix}{SpaceId}/{ChannelId}";

    public static bool TryParse(string? raw, [NotNullWhen(true)] out RadioRoomId? roomId)
    {
        roomId = null;
        if (raw is null || !raw.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var parts = raw[Prefix.Length..].Split('/');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var spaceId) || !Guid.TryParse(parts[1], out var channelId))
            return false;

        roomId = new RadioRoomId(spaceId, channelId);
        return true;
    }
}

/// <summary>A broadcaster's radio participant: <c>bc:{userId}</c>. Not a guid, so it never reaches a roster.</summary>
public record RadioIdentity([field: Id(0)] Guid UserId)
{
    public const string Prefix = "bc:";

    public string ToRawIdentity() => $"{Prefix}{UserId}";

    public static bool TryParse(string? raw, [NotNullWhen(true)] out RadioIdentity? identity)
    {
        identity = null;
        if (raw is null || !raw.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParse(raw.AsSpan(Prefix.Length), out var userId))
            return false;

        identity = new RadioIdentity(userId);
        return true;
    }
}
