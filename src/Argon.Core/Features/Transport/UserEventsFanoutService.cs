namespace Argon.Features.Transport;

using Argon.Core.Features.Transport;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using NATS.Client.Core;

/// <summary>
/// Background service that subscribes to NATS user event subjects for users
/// that have locally-connected SignalR clients, delivering cross-DC user events
/// (DMs, friend requests, calls) to local SignalR connections.
/// </summary>
public sealed class UserEventsFanoutService : BackgroundService
{
    private readonly INatsClient _natsClient;
    private readonly IHubContext<AppHub> _hubContext;
    private readonly ILogger<UserEventsFanoutService> _logger;
    private readonly string _localDcId;
    private readonly Channel<UserSubCommand> _commands = Channel.CreateUnbounded<UserSubCommand>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<Guid, int> _userRefCounts = new();

    public UserEventsFanoutService(
        INatsClient natsClient,
        IHubContext<AppHub> hubContext,
        IConfiguration configuration,
        ILogger<UserEventsFanoutService> logger)
    {
        _natsClient = natsClient;
        _hubContext = hubContext;
        _logger = logger;
        _localDcId = configuration.GetValue<string>("Datacenter:Id") ?? "local";
    }

    public static string ToUserEventsSubject(Guid userId) => $"user.events.{userId:N}";

    /// <summary>
    /// Called when a user connects to this DC. Ref-counted: NATS subscription
    /// starts on first connection, removed on last disconnect.
    /// </summary>
    public void TrackUser(Guid userId)
    {
        var count = _userRefCounts.AddOrUpdate(userId, 1, (_, c) => c + 1);
        if (count == 1)
            _commands.Writer.TryWrite(new(UserSubCommandKind.Subscribe, userId));
    }

    /// <summary>
    /// Called when a user disconnects from this DC.
    /// </summary>
    public void UntrackUser(Guid userId)
    {
        if (!_userRefCounts.TryGetValue(userId, out var current)) return;

        var newCount = current - 1;
        if (newCount <= 0)
        {
            _userRefCounts.TryRemove(userId, out _);
            _commands.Writer.TryWrite(new(UserSubCommandKind.Unsubscribe, userId));
        }
        else
        {
            _userRefCounts[userId] = newCount;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriptions = new Dictionary<Guid, CancellationTokenSource>();

        try
        {
            await foreach (var cmd in _commands.Reader.ReadAllAsync(stoppingToken))
            {
                switch (cmd.Kind)
                {
                    case UserSubCommandKind.Subscribe when !subscriptions.ContainsKey(cmd.UserId):
                        subscriptions[cmd.UserId] = StartLoop(cmd.UserId, stoppingToken);
                        break;

                    case UserSubCommandKind.Unsubscribe when subscriptions.Remove(cmd.UserId, out var cts):
                        await cts.CancelAsync();
                        cts.Dispose();
                        break;
                }
            }
        }
        finally
        {
            foreach (var cts in subscriptions.Values)
            {
                await cts.CancelAsync();
                cts.Dispose();
            }
            subscriptions.Clear();
        }
    }

    private CancellationTokenSource StartLoop(Guid userId, CancellationToken parent)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        _ = RunSubscription(userId, cts.Token);
        return cts;
    }

    private async Task RunSubscription(Guid userId, CancellationToken ct)
    {
        var subject = ToUserEventsSubject(userId);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Cross-DC user subscription started for {UserId}", userId);

                await foreach (var msg in _natsClient.SubscribeAsync<byte[]>(subject, cancellationToken: ct))
                {
                    if (msg.Data is null || msg.Data.Length == 0) continue;

                    // Skip events originating from this DC
                    if (msg.Headers?.TryGetValue("X-Source-Dc", out var sourceDc) == true
                        && sourceDc.ToString() == _localDcId)
                        continue;

                    await _hubContext.Clients.User(userId.ToString())
                        .SendAsync("forSelf", msg.Data, cancellationToken: ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cross-DC user subscription error for {UserId}, retrying in 2s", userId);
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private readonly record struct UserSubCommand(UserSubCommandKind Kind, Guid UserId);
    private enum UserSubCommandKind { Subscribe, Unsubscribe }
}
