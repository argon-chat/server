namespace Argon.Grains;

using Argon.Features.Integrations.Connections;
using Argon.Grains.Interfaces;

/// <inheritdoc cref="IConnectionsMaintenanceGrain"/>
public sealed class ConnectionsMaintenanceGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IConnectionProviderRegistry providers,
    TokenSealer sealer,
    IOptions<ConnectionsOptions> options,
    ILogger<ConnectionsMaintenanceGrain> logger) : Grain, IConnectionsMaintenanceGrain, IRemindable, IReminderJob
{
    private const string ReminderName = "connections-maintenance";

    /// <summary>Five minutes, as the other sweepers: a fleet restart is the wrong moment to add scan load.</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);

    private ConnectionsOptions Options => options.Value;

    string IReminderJob.ReminderName => ReminderName;

    ReminderSchedule? IReminderJob.Schedule
        => Options.Enabled ? new(FirstDelay, Options.MaintenanceInterval) : null;

    public ValueTask EnsureActiveAsync() => ValueTask.CompletedTask;

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReminderName)
            return;

        if (!Options.Enabled)
        {
            await this.DropReminderAsync(ReminderName);
            return;
        }

        try
        {
            var report = await RunOnceAsync(CancellationToken.None);

            logger.LogInformation("Connections maintenance: {Refreshed} tokens refreshed, {Resealed} re-sealed, {Reauth} need re-auth, {Details} details refreshed, {Failed} failed",
                report.TokensRefreshed, report.TokensResealed, report.TokensNeedReauth, report.DetailsRefreshed, report.Failed);
        }
        catch (Exception e)
        {
            // Never rethrown: Orleans would retry the tick, and the next one re-derives everything anyway.
            logger.LogError(e, "Connections maintenance failed; the next tick starts over");
        }
    }

    public async Task<ConnectionsMaintenanceReport> RunOnceAsync(CancellationToken ct = default)
    {
        var now       = DateTimeOffset.UtcNow;
        var batch     = Options.MaintenanceBatch;
        var refreshed = 0;
        var resealed  = 0;
        var reauth    = 0;
        var details   = 0;
        var failed    = 0;

        List<(Guid UserId, ConnectionProvider Provider)> tokenWork;
        var detailWork = new List<(Guid UserId, ConnectionProvider Provider)>();

        await using (var ctx = await context.CreateDbContextAsync(ct))
        {
            var expiring = now + Options.TokenRefreshLead;
            var idle     = now - Options.TokenKeepAliveEvery;
            var version  = (int)sealer.CurrentVersion;

            // A token due for renewal on a row nothing has touched for a keep-alive period, or any
            // row sealed under a retired key. A row in use is renewed when it is opened, so this
            // costs at most one refresh per idle connection per keep-alive period.
            tokenWork = (await ctx.UserConnections.AsNoTracking()
                   .Where(x => x.SealedTokens != null && x.Status == ConnectionStatus.ACTIVE
                            && ((x.AccessTokenExpiresAt != null && x.AccessTokenExpiresAt < expiring && x.UpdatedAt < idle)
                             || x.TokenKeyVersion != version))
                   .OrderBy(x => x.UpdatedAt)
                   .Take(batch)
                   .Select(x => new { x.UserId, x.Provider })
                   .ToListAsync(ct))
               .Select(x => (x.UserId, x.Provider))
               .ToList();

            foreach (var adapter in providers.Configured().Where(p => p.Capabilities.HasFlag(ConnectionCapability.DETAILS)))
            {
                var kind   = adapter.Kind;
                var before = now - Options.For(kind).DetailsRefreshEvery;

                detailWork.AddRange((await ctx.UserConnections.AsNoTracking()
                       .Where(x => x.Provider == kind && x.Status == ConnectionStatus.ACTIVE
                                && (x.DetailsRefreshedAt == null || x.DetailsRefreshedAt < before))
                       .OrderBy(x => x.DetailsRefreshedAt)
                       .Take(batch)
                       .Select(x => new { x.UserId, x.Provider })
                       .ToListAsync(ct))
                   .Select(x => (x.UserId, x.Provider)));
            }
        }

        foreach (var (userId, provider) in tokenWork)
        {
            try
            {
                switch (await GrainFactory.GetGrain<IUserConnectionsGrain>(userId).UpkeepTokenAsync(provider, ct))
                {
                    case TokenUpkeep.Refreshed:   refreshed++; break;
                    case TokenUpkeep.Resealed:    resealed++;  break;
                    case TokenUpkeep.NeedsReauth: reauth++;    break;
                    case TokenUpkeep.Failed:      failed++;    break;
                }
            }
            catch (Exception e)
            {
                failed++;
                logger.LogWarning(e, "Token upkeep for {UserId}/{Provider} failed", userId, provider);
            }
        }

        foreach (var (userId, provider) in detailWork)
        {
            try
            {
                var result = await GrainFactory.GetGrain<IUserConnectionsGrain>(userId).RefreshDetailsAsync(provider, force: true, ct);

                if (result.IsSuccess)
                    details++;
                else if (result.Error == ConnectionError.NEEDS_REAUTH)
                    reauth++;
                else
                    failed++;
            }
            catch (Exception e)
            {
                failed++;
                logger.LogWarning(e, "Details refresh for {UserId}/{Provider} failed", userId, provider);
            }
        }

        return new ConnectionsMaintenanceReport(refreshed, resealed, reauth, details, failed);
    }
}
