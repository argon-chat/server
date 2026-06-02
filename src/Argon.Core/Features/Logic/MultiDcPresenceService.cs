namespace Argon.Features.Logic;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Net;
using System.Text.Json;

/// <summary>
/// Multi-DC presence decorator. Wraps the local <see cref="UserPresenceService"/>
/// and publishes presence changes to NATS so other DCs can update their local Redis.
/// Also subscribes to presence events from other DCs and writes them locally.
///
/// Architecture:
/// - Each DC maintains its own Redis presence keys (local sessions)
/// - On session online/offline, publishes to NATS `presence.events.{dcId}`
/// - Other DCs subscribe and create "shadow" presence keys with same TTL
/// - Shadow keys use a different prefix: `presence:remote:{userId}:session:{dcId}:{sessionId}`
/// - IsUserOnline checks both local AND remote keys
/// </summary>
public sealed class MultiDcPresenceService : BackgroundService
{
    private readonly IUserPresenceService _localPresence;
    private readonly INatsClient _nats;
    private readonly string _dcId;
    private readonly ILogger<MultiDcPresenceService> _logger;

    private const string PresenceSubjectPrefix = "presence.events.";

    public MultiDcPresenceService(
        IUserPresenceService localPresence,
        INatsClient nats,
        IConfiguration configuration,
        ILogger<MultiDcPresenceService> logger)
    {
        _localPresence = localPresence;
        _nats = nats;
        _dcId = configuration["Datacenter:Id"] ?? "local";
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe to presence events from all OTHER DCs
        var subject = $"{PresenceSubjectPrefix}>";

        _logger.LogInformation("MultiDcPresenceService started on DC {DcId}, subscribing to {Subject}",
            _dcId, subject);

        try
        {
            await foreach (var msg in _nats.SubscribeAsync<byte[]>(subject, cancellationToken: stoppingToken))
            {
                try
                {
                    await HandleRemotePresenceEventAsync(msg, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to handle remote presence event");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Publishes a session online event to NATS for cross-DC visibility.
    /// Called by AppHub after local presence is set.
    /// </summary>
    public async Task AnnounceSessionOnlineAsync(Guid userId, string sessionId, UserStatus status)
    {
        var evt = new PresenceChangeEvent(
            userId, sessionId, _dcId, PresenceChangeKind.Online, status, DateTimeOffset.UtcNow);
        await PublishEventAsync(evt);
    }

    /// <summary>
    /// Publishes a session offline event to NATS.
    /// </summary>
    public async Task AnnounceSessionOfflineAsync(Guid userId, string sessionId)
    {
        var evt = new PresenceChangeEvent(
            userId, sessionId, _dcId, PresenceChangeKind.Offline, UserStatus.Offline, DateTimeOffset.UtcNow);
        await PublishEventAsync(evt);
    }

    /// <summary>
    /// Publishes a status change event to NATS.
    /// </summary>
    public async Task AnnounceStatusChangeAsync(Guid userId, string sessionId, UserStatus status)
    {
        var evt = new PresenceChangeEvent(
            userId, sessionId, _dcId, PresenceChangeKind.StatusChange, status, DateTimeOffset.UtcNow);
        await PublishEventAsync(evt);
    }

    /// <summary>
    /// Publishes a heartbeat to refresh shadow session TTLs on remote DCs.
    /// Called periodically (~60s) from UserSessionGrain's timer tick.
    /// </summary>
    public async Task AnnounceHeartbeatAsync(Guid userId, string sessionId)
    {
        var evt = new PresenceChangeEvent(
            userId, sessionId, _dcId, PresenceChangeKind.Heartbeat, UserStatus.Online, DateTimeOffset.UtcNow);
        await PublishEventAsync(evt);
    }

    private async Task PublishEventAsync(PresenceChangeEvent evt)
    {
        try
        {
            var subject = $"{PresenceSubjectPrefix}{_dcId}";
            var data = JsonSerializer.SerializeToUtf8Bytes(evt);
            await _nats.PublishAsync(subject, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish presence event for user {UserId}", evt.UserId);
        }
    }

    private async Task HandleRemotePresenceEventAsync(NatsMsg<byte[]> msg, CancellationToken ct)
    {
        if (msg.Data is null || msg.Data.Length == 0)
            return;

        var evt = JsonSerializer.Deserialize<PresenceChangeEvent>(msg.Data);
        if (evt is null)
            return;

        // Skip our own events
        if (evt.SourceDcId == _dcId)
            return;

        // Use the same key patterns as local presence so IsUserOnlineAsync/GetActiveSessionIdsAsync
        // automatically picks up remote sessions. Session ID is prefixed with DC to avoid collisions.
        var remoteSessionId = $"{evt.SourceDcId}:{evt.SessionId}";

        switch (evt.Kind)
        {
            case PresenceChangeKind.Online:
                await _localPresence.SetSessionOnlineAsync(evt.UserId, remoteSessionId, ct);
                await _localPresence.SetSessionStatusAsync(evt.UserId, remoteSessionId, evt.Status, ct);
                break;

            case PresenceChangeKind.Offline:
                await _localPresence.RemoveSessionAsync(evt.UserId, remoteSessionId, ct);
                await _localPresence.RemoveSessionStatusAsync(evt.UserId, remoteSessionId, ct);
                break;

            case PresenceChangeKind.StatusChange:
                await _localPresence.SetSessionStatusAsync(evt.UserId, remoteSessionId, evt.Status, ct);
                break;

            case PresenceChangeKind.Heartbeat:
                await _localPresence.HeartbeatAsync(evt.UserId, remoteSessionId, ct);
                await _localPresence.RefreshSessionStatusTtlAsync(evt.UserId, remoteSessionId, ct);
                break;
        }
    }
}

public sealed record PresenceChangeEvent(
    Guid UserId,
    string SessionId,
    string SourceDcId,
    PresenceChangeKind Kind,
    UserStatus Status,
    DateTimeOffset Timestamp);

public enum PresenceChangeKind
{
    Online,
    Offline,
    StatusChange,
    Heartbeat
}
