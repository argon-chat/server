namespace Argon.Features.EphemeralState;

/// <summary>
/// Abstraction for hot ephemeral state storage (voice channels, session routing).
/// Implementations: Redis (default), Aerospike (high-scale).
/// All data is per-DC with cross-DC sync handled externally via NATS events.
/// </summary>
public interface IEphemeralStateStore
{
    // --- Voice Channel State ---

    Task<Dictionary<Guid, VoiceChannelMember>> GetVoiceChannelMembersAsync(
        Guid channelId, CancellationToken ct = default);

    Task SetVoiceChannelMemberAsync(
        Guid channelId, Guid userId, VoiceChannelMember member, CancellationToken ct = default);

    Task RemoveVoiceChannelMemberAsync(
        Guid channelId, Guid userId, CancellationToken ct = default);

    Task RemoveAllVoiceChannelMembersAsync(
        Guid channelId, CancellationToken ct = default);

    // --- Voice Reverse Index (userId → which channel in which space) ---

    Task<VoiceSlotEntry?> GetUserVoiceSlotAsync(
        Guid spaceId, Guid userId, CancellationToken ct = default);

    Task SetUserVoiceSlotAsync(
        Guid spaceId, Guid userId, VoiceSlotEntry slot, CancellationToken ct = default);

    Task RemoveUserVoiceSlotAsync(
        Guid spaceId, Guid userId, CancellationToken ct = default);

    Task<Dictionary<Guid, VoiceSlotEntry>> GetAllVoiceSlotsAsync(
        Guid spaceId, CancellationToken ct = default);

    // --- Session Routing (which DC owns a user's session) ---

    Task SetSessionRouteAsync(
        string sessionId, SessionRoute route, TimeSpan ttl, CancellationToken ct = default);

    Task RefreshSessionRouteAsync(
        string sessionId, TimeSpan ttl, CancellationToken ct = default);

    Task RemoveSessionRouteAsync(
        string sessionId, CancellationToken ct = default);

    Task<SessionRoute?> GetSessionRouteAsync(
        string sessionId, CancellationToken ct = default);

    Task<List<SessionRoute>> GetUserSessionRoutesAsync(
        Guid userId, CancellationToken ct = default);
}

public sealed record VoiceChannelMember(
    Guid UserId,
    string SessionId,
    ChannelMemberState State,
    DateTimeOffset JoinedAt
);

public sealed record VoiceSlotEntry(
    Guid ChannelId,
    DateTimeOffset JoinedAt
);

public sealed record SessionRoute(
    string SessionId,
    Guid UserId,
    string DatacenterId,
    string EntryPointId,
    DateTimeOffset ConnectedAt
);
