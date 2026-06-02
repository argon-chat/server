namespace Argon.Features.Regions;

using Argon.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Manual admin operations for CockroachDB region management.
/// These are explicit, irreversible actions — not triggered automatically.
///
/// Workflow for adding a new region:
///   1. Deploy new DC → it auto-registers in `datacenters` table
///   2. Users from that DC are served data from nearest existing region (CockroachDB routes transparently)
///   3. Admin calls <see cref="EnrollRegionAsync"/> when ready to localize data placement
///   4. CockroachDB starts replicating data to the new region
///
/// Workflow for removing a region:
///   1. Admin calls <see cref="DecommissionAsync"/> for the DC
///   2. CockroachDB drops the region → replicas migrate to remaining regions
///   3. DC entity is soft-deleted
/// </summary>
public sealed class RegionOperations(
    IServiceScopeFactory scopeFactory,
    ILogger<RegionOperations> logger)
{
    /// <summary>
    /// Enrolls a datacenter's CockroachDB region for data locality.
    /// After this, REGIONAL BY ROW tables will place leaseholders in this region
    /// for rows belonging to users in this DC.
    /// </summary>
    public async Task EnrollRegionAsync(string dcId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dc = await db.Datacenters.FindAsync([dcId], ct)
                 ?? throw new InvalidOperationException($"Datacenter '{dcId}' not found");

        var region = dc.CockroachRegion;
        if (!ValidateRegionName(region))
            throw new InvalidOperationException($"Invalid region name: '{region}'");

        // Check if already enrolled
        var existingRegions = await db.Database
            .SqlQueryRaw<string>("SELECT region FROM [SHOW REGIONS FROM DATABASE]")
            .ToListAsync(ct);

        if (existingRegions.Contains(region))
        {
            logger.LogInformation("Region {Region} already enrolled in CockroachDB", region);
            return;
        }

        #pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync(
            $"ALTER DATABASE currentdb ADD REGION \"{region}\"", ct);
        #pragma warning restore EF1002

        logger.LogWarning("Enrolled CockroachDB region {Region} for DC {DcId}. " +
                          "Data will begin replicating to this region.", region, dcId);
    }

    /// <summary>
    /// Permanently decommissions a datacenter and optionally drops its CockroachDB region.
    /// Only drops the region if no other active DC uses it.
    /// </summary>
    public async Task DecommissionAsync(string dcId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dc = await db.Datacenters.FindAsync([dcId], ct)
                 ?? throw new InvalidOperationException($"Datacenter '{dcId}' not found");

        var region = dc.CockroachRegion;

        // Only drop region if no other active DC uses it
        var otherDcsInRegion = await db.Datacenters
            .Where(x => x.Id != dcId && x.CockroachRegion == region && !x.IsDeleted)
            .AnyAsync(ct);

        if (!otherDcsInRegion && ValidateRegionName(region))
        {
            var existingRegions = await db.Database
                .SqlQueryRaw<string>("SELECT region FROM [SHOW REGIONS FROM DATABASE]")
                .ToListAsync(ct);

            if (existingRegions.Contains(region))
            {
                #pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER DATABASE currentdb DROP REGION \"{region}\"", ct);
                #pragma warning restore EF1002

                logger.LogWarning("Dropped CockroachDB region {Region} (DC {DcId} decommissioned). " +
                                  "Replicas will migrate to remaining regions.", region, dcId);
            }
        }

        dc.Status = DatacenterStatus.Offline;
        dc.IsDeleted = true;
        dc.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogWarning("Datacenter {DcId} decommissioned", dcId);
    }

    /// <summary>
    /// Lists all registered CockroachDB regions and their enrollment status.
    /// </summary>
    public async Task<List<RegionStatus>> GetRegionStatusAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var enrolledRegions = await db.Database
            .SqlQueryRaw<string>("SELECT region FROM [SHOW REGIONS FROM DATABASE]")
            .ToListAsync(ct);

        var dcs = await db.Datacenters
            .Where(x => !x.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        return dcs
            .GroupBy(x => x.CockroachRegion)
            .Select(g => new RegionStatus
            {
                Region = g.Key,
                IsEnrolled = enrolledRegions.Contains(g.Key),
                Datacenters = g.Select(dc => new DatacenterInfo
                {
                    Id = dc.Id,
                    DisplayName = dc.DisplayName,
                    Status = dc.Status,
                    LastHeartbeatAt = dc.LastHeartbeatAt
                }).ToList()
            })
            .ToList();
    }

    private static bool ValidateRegionName(string region)
        => System.Text.RegularExpressions.Regex.IsMatch(region, @"^[a-zA-Z0-9\-]+$");
}

public sealed class RegionStatus
{
    public required string Region { get; init; }
    public required bool IsEnrolled { get; init; }
    public required List<DatacenterInfo> Datacenters { get; init; }
}

public sealed class DatacenterInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required DatacenterStatus Status { get; init; }
    public required DateTimeOffset LastHeartbeatAt { get; init; }
}
