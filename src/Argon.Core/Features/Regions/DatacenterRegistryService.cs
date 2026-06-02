namespace Argon.Features.Regions;

using Argon.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

/// <summary>
/// Registers this datacenter in the shared database on startup and periodically heartbeats.
/// Does NOT modify CockroachDB topology — region ADD/DROP is a manual admin operation
/// via <see cref="RegionOperations"/>. Until the region is registered in CockroachDB,
/// data for users in this DC is stored in the nearest registered region.
/// </summary>
public sealed class DatacenterRegistryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatacenterRegistryService> _logger;
    private readonly DatacenterRegistryOptions _options;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(2);

    public DatacenterRegistryService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DatacenterRegistryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = new DatacenterRegistryOptions
        {
            Id = configuration.GetValue<string>("Datacenter:Id") ?? "local",
            DisplayName = configuration.GetValue<string>("Datacenter:DisplayName") ?? "Local",
            CockroachRegion = configuration.GetValue<string>("Datacenter:CockroachRegion")
                              ?? configuration.GetValue<string>("Database:Regions:PrimaryRegion")
                              ?? "ru-central",
            NatsGatewayUrl = configuration.GetValue<string>("Datacenter:NatsGatewayUrl"),
            PublicEndpoint = configuration.GetValue<string>("Datacenter:PublicEndpoint"),
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        await RegisterAsync(stoppingToken);

        _logger.LogInformation("Datacenter {DcId} registered (region {Region}), heartbeat started",
            _options.Id, _options.CockroachRegion);

        using var timer = new PeriodicTimer(HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await HeartbeatAsync(stoppingToken);
        }
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var existing = await db.Datacenters.FindAsync([_options.Id], ct);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            db.Datacenters.Add(new DatacenterEntity
            {
                Id = _options.Id,
                DisplayName = _options.DisplayName,
                CockroachRegion = _options.CockroachRegion,
                NatsGatewayUrl = _options.NatsGatewayUrl,
                PublicEndpoint = _options.PublicEndpoint,
                Status = DatacenterStatus.Online,
                LastHeartbeatAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                Metadata = BuildMetadata()
            });
            _logger.LogInformation("Registered new datacenter {DcId}", _options.Id);
        }
        else
        {
            existing.DisplayName = _options.DisplayName;
            existing.CockroachRegion = _options.CockroachRegion;
            existing.NatsGatewayUrl = _options.NatsGatewayUrl;
            existing.PublicEndpoint = _options.PublicEndpoint;
            existing.Status = DatacenterStatus.Online;
            existing.LastHeartbeatAt = now;
            existing.UpdatedAt = now;
            existing.Metadata = BuildMetadata();
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var now = DateTimeOffset.UtcNow;

            await db.Datacenters
                .Where(x => x.Id == _options.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.LastHeartbeatAt, now)
                    .SetProperty(x => x.Status, DatacenterStatus.Online), ct);

            // Mark stale DCs as offline (informational — no topology action)
            var staleThreshold = now - StaleThreshold;
            await db.Datacenters
                .Where(x => x.Id != _options.Id
                            && x.Status == DatacenterStatus.Online
                            && x.LastHeartbeatAt < staleThreshold)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, DatacenterStatus.Offline), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat failed for DC {DcId}", _options.Id);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await db.Datacenters
                .Where(x => x.Id == _options.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, DatacenterStatus.Draining), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark DC {DcId} as draining on shutdown", _options.Id);
        }

        await base.StopAsync(cancellationToken);
    }

    private string BuildMetadata() => JsonSerializer.Serialize(new
    {
        MachineName = Environment.MachineName,
        StartedAt = DateTimeOffset.UtcNow,
        Version = typeof(DatacenterRegistryService).Assembly.GetName().Version?.ToString() ?? "0.0.0"
    });
}

internal sealed class DatacenterRegistryOptions
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string CockroachRegion { get; init; }
    public string? NatsGatewayUrl { get; init; }
    public string? PublicEndpoint { get; init; }
}
