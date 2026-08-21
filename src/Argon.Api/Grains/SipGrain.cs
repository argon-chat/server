namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Grains.Interfaces;
using Grains.Interfaces;
using Orleans.Concurrency;

/// <summary>
/// Handles USSD-style command codes dialed by users. Currently routes feature-flag
/// activation codes; the router is structured so future USSD commands slot in alongside.
/// SIP dialing methods are intentionally not implemented yet.
/// </summary>
[StatelessWorker]
public sealed class SipGrain(
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    ILogger<SipGrain> logger) : Grain, ISipGrain
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

    public Task<IDialCheckResult> BeginDialCheck(Guid userId, Guid phoneId, CancellationToken ct = default)
        => throw new NotSupportedException("SIP dialing is not implemented");

    public Task<IBeginCallResult> DialUp(Guid userId, Guid phoneId, Guid corlId, CancellationToken ct = default)
        => throw new NotSupportedException("SIP dialing is not implemented");

    public Task HangupCall(Guid userId, Guid callId, CancellationToken ct = default)
        => throw new NotSupportedException("SIP dialing is not implemented");

    private async Task NotifyAsync<T>(Guid userId, T payload) where T : IArgonEvent
    {
        var sessions = await sessionDiscovery.GetUserSessionsAsync(userId);
        if (sessions.Count == 0)
            return;

        await notifier.NotifySessionsAsync(sessions, payload);
    }
}
