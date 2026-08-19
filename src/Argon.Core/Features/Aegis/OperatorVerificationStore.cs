namespace Argon.Features.Aegis;

using Argon.Services;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

/// <summary>
/// What touching the hardware key proved, held between the step-up and the token it unlocks.
/// </summary>
public record OperatorVerificationState(
    Guid OperatorId,
    string OperatorEmail,
    string CertThumbprint,
    string DisplayName,
    bool IsSystemOperator);

/// <summary>
/// Where a completed operator step-up is remembered until the flow that needed it finishes.
/// </summary>
/// <remarks>
/// Server-side, in Redis, keyed by the user — never in the session cookie. A cookie is handed to the
/// browser, and "this user has proved they are an operator" is exactly the claim a browser must not
/// be trusted to carry. Redis also gives it the two properties the flow depends on: it expires by
/// itself, and it can be deleted the moment it is spent.
/// <para>
/// Spent, not merely expired — <see cref="ConsumeAsync"/> is called once a token has been issued, so
/// authorizing a second internal application asks for the key again rather than riding the first
/// verification to the end of its window.
/// </para>
/// </remarks>
public interface IOperatorVerificationStore
{
    Task StoreAsync(Guid userId, OperatorVerificationState state, CancellationToken ct = default);

    Task<OperatorVerificationState?> ReadAsync(Guid userId, CancellationToken ct = default);

    Task<bool> IsVerifiedAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Forgets the verification, whether it was used or the user simply moved on.</summary>
    Task ConsumeAsync(Guid userId, CancellationToken ct = default);
}

public sealed class OperatorVerificationStore(
    IArgonCacheDatabase cache,
    IOptions<OperatorMutualTlsOptions> options) : IOperatorVerificationStore
{
    private static string KeyFor(Guid userId)
        => $"aegis:operator-verified:{userId}";

    public Task StoreAsync(Guid userId, OperatorVerificationState state, CancellationToken ct = default)
        => cache.StringSetAsync(KeyFor(userId), JsonConvert.SerializeObject(state),
            options.Value.VerificationLifetime, ct);

    public async Task<OperatorVerificationState?> ReadAsync(Guid userId, CancellationToken ct = default)
    {
        var stored = await cache.StringGetAsync(KeyFor(userId), ct);

        return string.IsNullOrEmpty(stored)
            ? null
            : JsonConvert.DeserializeObject<OperatorVerificationState>(stored);
    }

    public Task<bool> IsVerifiedAsync(Guid userId, CancellationToken ct = default)
        => cache.KeyExistsAsync(KeyFor(userId), ct);

    public Task ConsumeAsync(Guid userId, CancellationToken ct = default)
        => cache.KeyDeleteAsync(KeyFor(userId), ct);
}
