namespace Argon.Features.BotApi;

/// <summary>
/// Verifies Bot API contract hashes on application startup.
/// If any <see cref="StableContractAttribute"/> hash doesn't match the actual
/// API surface, throws and prevents the server from starting.
/// </summary>
public sealed class BotContractVerificationStartupFilter(ILogger<BotContractVerificationStartupFilter> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken ct)
    {
        var mismatches = BotContractVerifier.Verify();

        if (mismatches.Count == 0)
        {
            var manifest = BotContractVerifier.GenerateManifest();
            var stableCount = manifest.Count(m => m.IsStable);
            logger.LogInformation(
                "Bot API contract check passed. {Total} interfaces, {Stable} stable",
                manifest.Count, stableCount);
            return Task.CompletedTask;
        }

        foreach (var m in mismatches)
        {
            logger.LogCritical(
                "Bot API CONTRACT BREAK: {Interface} declared hash {Declared} but computed {Computed}. " +
                "Either revert the breaking change or update the [StableContract] hash via 'dotnet run -- bot-api rehash'",
                m.InterfaceName, m.DeclaredHash, m.ComputedHash);
        }

        throw new InvalidOperationException(
            $"Bot API contract verification failed: {mismatches.Count} stable interface(s) have hash mismatches. " +
            "This means the API surface was modified after being marked as stable. " +
            "See logs above for details.");
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
}
