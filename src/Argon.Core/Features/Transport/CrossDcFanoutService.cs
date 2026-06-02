namespace Argon.Features.Transport;

using Argon.Core.Features.Transport;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using NATS.Client.Core;

/// <summary>
/// Background service that subscribes to NATS space event subjects for spaces
/// that have locally-connected SignalR clients, and pushes received events
/// to local SignalR groups. This enables cross-DC event delivery.
/// </summary>
public sealed class CrossDcFanoutService : BackgroundService
{
    private readonly INatsClient _natsClient;
    private readonly IHubContext<AppHub> _hubContext;
    private readonly ISpaceSubscriptionTracker _tracker;
    private readonly ILogger<CrossDcFanoutService> _logger;
    private readonly string _localDcId;
    private readonly Channel<SubCommand> _commands = Channel.CreateUnbounded<SubCommand>(
        new UnboundedChannelOptions { SingleReader = true });

    public CrossDcFanoutService(
        INatsClient natsClient,
        IHubContext<AppHub> hubContext,
        ISpaceSubscriptionTracker tracker,
        IConfiguration configuration,
        ILogger<CrossDcFanoutService> logger)
    {
        _natsClient = natsClient;
        _hubContext = hubContext;
        _tracker = tracker;
        _logger = logger;
        _localDcId = configuration.GetValue<string>("Datacenter:Id") ?? "local";
    }

    public static string ToSpaceEventsSubject(Guid spaceId) => $"space.events.{spaceId:N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tracker.SpaceSubscribed += OnSpaceSubscribed;
        _tracker.SpaceUnsubscribed += OnSpaceUnsubscribed;

        var subscriptions = new Dictionary<Guid, CancellationTokenSource>();

        try
        {
            // Seed from already-active spaces
            foreach (var spaceId in _tracker.GetActiveSpaces())
                subscriptions[spaceId] = StartLoop(spaceId, stoppingToken);

            await foreach (var cmd in _commands.Reader.ReadAllAsync(stoppingToken))
            {
                switch (cmd.Kind)
                {
                    case SubCommandKind.Subscribe when !subscriptions.ContainsKey(cmd.SpaceId):
                        subscriptions[cmd.SpaceId] = StartLoop(cmd.SpaceId, stoppingToken);
                        break;

                    case SubCommandKind.Unsubscribe when subscriptions.Remove(cmd.SpaceId, out var cts):
                        await cts.CancelAsync();
                        cts.Dispose();
                        break;
                }
            }
        }
        finally
        {
            _tracker.SpaceSubscribed -= OnSpaceSubscribed;
            _tracker.SpaceUnsubscribed -= OnSpaceUnsubscribed;

            foreach (var cts in subscriptions.Values)
            {
                await cts.CancelAsync();
                cts.Dispose();
            }
            subscriptions.Clear();
        }
    }

    private CancellationTokenSource StartLoop(Guid spaceId, CancellationToken parent)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        _ = RunSubscription(spaceId, cts.Token);
        return cts;
    }

    private async Task RunSubscription(Guid spaceId, CancellationToken ct)
    {
        var subject = ToSpaceEventsSubject(spaceId);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Cross-DC subscription started for space {SpaceId}", spaceId);

                await foreach (var msg in _natsClient.SubscribeAsync<byte[]>(subject, cancellationToken: ct))
                {
                    if (msg.Data is null || msg.Data.Length == 0) continue;

                    if (msg.Headers?.TryGetValue("X-Source-Dc", out var sourceDc) == true
                        && sourceDc.ToString() == _localDcId)
                        continue;

                    await _hubContext.Clients.Group($"spaces/{spaceId}")
                        .SendAsync("broadcastSpace", msg.Data, cancellationToken: ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cross-DC subscription error for space {SpaceId}, retrying in 2s", spaceId);
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void OnSpaceSubscribed(Guid spaceId)
        => _commands.Writer.TryWrite(new(SubCommandKind.Subscribe, spaceId));

    private void OnSpaceUnsubscribed(Guid spaceId)
        => _commands.Writer.TryWrite(new(SubCommandKind.Unsubscribe, spaceId));

    private readonly record struct SubCommand(SubCommandKind Kind, Guid SpaceId);
    private enum SubCommandKind { Subscribe, Unsubscribe }
}
