namespace Argon.Sfu;

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