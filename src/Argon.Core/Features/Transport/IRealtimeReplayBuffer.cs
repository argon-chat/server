namespace Argon.Core.Features.Transport;

using Argon.Services;
using StackExchange.Redis;

/// <summary>
/// A short-lived replay log for realtime events, backed by Redis Streams.
///
/// Every event delivered over SignalR (<c>broadcastSpace</c> / <c>forSelf</c>) is also
/// appended to a per-destination stream whose native entry id acts as a monotonic cursor.
/// When a client briefly loses the hub connection (VPN switch, network blip) the events
/// sent during the gap are gone from SignalR's perspective — but they remain in the stream
/// for a retention window. On reconnect the client presents its last-seen cursor and we
/// replay everything after it, so no events are lost.
///
/// If the client's cursor is older than the retention window (so entries may already have
/// been trimmed) we report a gap and the client falls back to a full state reload.
/// </summary>
public interface IRealtimeReplayBuffer
{
    Task<string> AppendUserAsync(Guid userId, ReadOnlyMemory<byte> payload, CancellationToken ct = default);
    Task<string> AppendSpaceAsync(Guid spaceId, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    Task<ReplayReadResult> ReadUserSinceAsync(Guid userId, string? sinceId, CancellationToken ct = default);
    Task<ReplayReadResult> ReadSpaceSinceAsync(Guid spaceId, string? sinceId, CancellationToken ct = default);
}

public readonly record struct ReplayEntry(string Id, byte[] Payload);

/// <param name="Entries">Events strictly after the requested cursor, in order.</param>
/// <param name="Gap">True when continuity can't be guaranteed (cursor trimmed / too many
/// events) and the client should do a full resync instead of trusting the replay.</param>
public readonly record struct ReplayReadResult(IReadOnlyList<ReplayEntry> Entries, bool Gap);

public sealed class RedisRealtimeReplayBuffer(IRedisPoolConnections pool) : IRealtimeReplayBuffer
{
    private const string PayloadField = "p";

    // How long events stay replayable. Covers short reconnects; long outages fall back to
    // a full resync anyway, so there's no point retaining more.
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);

    // Idle streams (space with no traffic, offline user) are reaped by key TTL, refreshed
    // on every append. A bit longer than Retention so an active stream never expires mid-use.
    private static readonly TimeSpan KeyTtl = TimeSpan.FromMinutes(10);

    // Hard cap on a single replay. More missed events than this within the window means the
    // client was gone long enough that a full resync is cheaper and safer.
    private const int MaxReplay = 2048;

    private static string UserKey(Guid userId)   => $"rt:u:{userId:N}";
    private static string SpaceKey(Guid spaceId) => $"rt:s:{spaceId:N}";

    public Task<string> AppendUserAsync(Guid userId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => AppendAsync(UserKey(userId), payload);

    public Task<string> AppendSpaceAsync(Guid spaceId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => AppendAsync(SpaceKey(spaceId), payload);

    public Task<ReplayReadResult> ReadUserSinceAsync(Guid userId, string? sinceId, CancellationToken ct = default)
        => ReadSinceAsync(UserKey(userId), sinceId);

    public Task<ReplayReadResult> ReadSpaceSinceAsync(Guid spaceId, string? sinceId, CancellationToken ct = default)
        => ReadSinceAsync(SpaceKey(spaceId), sinceId);

    private async Task<string> AppendAsync(string key, ReadOnlyMemory<byte> payload)
    {
        await using var scope = pool.Rent();
        var             db    = scope.GetDatabase();

        // Trim by MINID and append atomically: drop everything older than the retention
        // window, then add the new entry with a server-generated id ('*').
        var minId = $"{(DateTimeOffset.UtcNow - Retention).ToUnixTimeMilliseconds()}-0";

        var result = await db.ExecuteAsync(
            "XADD", key, "MINID", "~", minId, "*", PayloadField, (byte[])payload.ToArray());

        await db.KeyExpireAsync(key, KeyTtl);

        return (string)result!;
    }

    private async Task<ReplayReadResult> ReadSinceAsync(string key, string? sinceId)
    {
        // No cursor → first connect of this client; it already has fresh state, nothing to replay.
        if (string.IsNullOrEmpty(sinceId))
            return new ReplayReadResult([], Gap: false);

        // Cursor older than what we could possibly have retained → entries may have been
        // trimmed, so we can't guarantee continuity. Tell the client to resync fully.
        if (IsCursorTooOld(sinceId))
            return new ReplayReadResult([], Gap: true);

        await using var scope = pool.Rent();
        var             db    = scope.GetDatabase();

        // Exclusive lower bound: '(' prefix returns entries strictly after sinceId (Redis 6.2+).
        var entries = await db.StreamRangeAsync(
            key, minId: "(" + sinceId, maxId: "+", count: MaxReplay + 1);

        // Hit the cap → too far behind, prefer a full resync over a partial replay.
        if (entries.Length > MaxReplay)
            return new ReplayReadResult([], Gap: true);

        var list = new List<ReplayEntry>(entries.Length);
        foreach (var e in entries)
        {
            var val = e[PayloadField];
            if (val.IsNull)
                continue;
            list.Add(new ReplayEntry(e.Id!, (byte[])val!));
        }

        return new ReplayReadResult(list, Gap: false);
    }

    private static bool IsCursorTooOld(string sinceId)
    {
        // Stream ids are "<unixMs>-<seq>". If the ms part predates the retention cutoff the
        // cursor is unreplayable. Malformed ids are treated as too old (force a resync).
        var dash  = sinceId.IndexOf('-');
        var msSpan = dash < 0 ? sinceId : sinceId[..dash];
        if (!long.TryParse(msSpan, out var ms))
            return true;

        var cutoff = (DateTimeOffset.UtcNow - Retention).ToUnixTimeMilliseconds();
        return ms < cutoff;
    }
}
