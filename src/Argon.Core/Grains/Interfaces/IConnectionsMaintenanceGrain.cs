namespace Argon.Grains.Interfaces;

public sealed record ConnectionsMaintenanceReport(int TokensRefreshed, int TokensResealed, int TokensNeedReauth, int DetailsRefreshed, int Failed)
{
    public static readonly ConnectionsMaintenanceReport Empty = new(0, 0, 0, 0, 0);
}

/// <summary>
/// The hourly pass over linked accounts: tokens refreshed before they expire, rows re-sealed after
/// a key rotation, details fetched again once they are older than the provider's interval.
/// </summary>
/// <remarks>
/// A reminder, like <c>TtlSweepGrain</c>, first firing five minutes after activation so a fleet
/// restart does not add scan load to the first minutes. Each row's work goes through the owner's
/// <c>IUserConnectionsGrain</c>, which keeps that grain the single writer.
/// </remarks>
[Alias("Argon.Grains.Interfaces.IConnectionsMaintenanceGrain")]
public interface IConnectionsMaintenanceGrain : IGrainWithGuidKey
{
    /// <summary>One activation per cluster; the startup task of the role that hosts it activates this key.</summary>
    static readonly Guid SingletonId = Guid.Parse("a0a0a0a0-dead-beef-0000-000000000004");

    [Alias(nameof(EnsureActiveAsync))]
    ValueTask EnsureActiveAsync();

    [Alias(nameof(RunOnceAsync))]
    Task<ConnectionsMaintenanceReport> RunOnceAsync(CancellationToken ct = default);
}
