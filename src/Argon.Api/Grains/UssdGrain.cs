namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Grains.Interfaces;
using Grains.Interfaces;
using Orleans.Concurrency;

/// <summary>
/// Handles USSD-style command codes dialed by users. Currently routes feature-flag
/// activation codes; the router is structured so future USSD commands slot in alongside.
/// </summary>
[StatelessWorker]
public sealed class UssdGrain(
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    ILogger<UssdGrain> logger) : Grain, IUssdGrain
{
    public async Task<ServiceUssdResult> UssdExecute(Guid userId, string ussd, Guid corlId, CancellationToken ct = default)
    {
        var code = (ussd ?? string.Empty).Trim();
        if (code.Length == 0)
            return new ServiceUssdResult(false, "Empty USSD command");

        var flags = GrainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);

        // Route 1: feature-flag activation by code.
        var flagId = await flags.FindFlagIdByUssdCodeAsync(code);
        if (flagId is null)
            return new ServiceUssdResult(false, "Unknown USSD command");

        var result = await flags.ActivateForUserAsync(userId, flagId);
        if (!result.IsEnabled)
            return new ServiceUssdResult(false, "Activation failed");

        await NotifyAsync(userId, new FeatureFlagActivated(userId, flagId, true, result.Variant));

        logger.LogInformation("USSD activated feature flag {FlagId} for user {UserId}", flagId, userId);
        return new ServiceUssdResult(true, $"Feature '{flagId}' activated");
    }

    private async Task NotifyAsync<T>(Guid userId, T payload) where T : IArgonEvent
    {
        var sessions = await sessionDiscovery.GetUserSessionsAsync(userId);
        if (sessions.Count == 0)
            return;

        await notifier.NotifySessionsAsync(sessions, payload);
    }
}
