namespace Argon.Grains.Interfaces;

/// <summary>
/// What an account looks like on an account picker.
/// </summary>
public record UserBasicInfo(Guid UserId, string Username, string? AvatarFileId);

/// <summary>
/// The operator record behind a user account, if there is one.
/// </summary>
public record OperatorBasicInfo(
    Guid OperatorId,
    string Email,
    string DisplayName,
    bool HasActiveCertificate,
    bool IsActive,
    bool IsSystemOperator);

/// <summary>
/// One operator's grant on one application.
/// </summary>
/// <param name="AllowedScopes">
/// Scopes the operator may ask for on this app. Empty means the app's own scope list applies
/// unnarrowed.
/// </param>
/// <param name="Claims">App-specific claims to put in the operator's token.</param>
public record OperatorAppAccessInfo(
    Guid OperatorId,
    Guid AppId,
    List<string> AllowedScopes,
    List<string> Claims,
    bool IsActive);

/// <summary>
/// The read-only lookups the identity server does about people, as opposed to about applications.
/// </summary>
/// <remarks>
/// Stateless, keyed by <see cref="Guid.Empty"/>: every call is a query and none of them own state,
/// so the worker pool sizes itself. It exists as a grain rather than a repository because database
/// access lives in grains — <c>aegis</c> is a client role and opens no connection of its own.
/// <para>
/// Application-side lookups are <see cref="IDevTeamsGrain"/>'s and the policy over them is
/// <see cref="IAppsManagementGrain"/>'s. What is left, and lives here, is the two things the OAuth
/// flow needs to know about the human: which account is signing in, and whether that account is also
/// an operator.
/// </para>
/// </remarks>
[Alias("Argon.Grains.Interfaces.IIdentityDirectoryGrain")]
public interface IIdentityDirectoryGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetUserBasicInfoAsync))]
    Task<UserBasicInfo?> GetUserBasicInfoAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The account a mailbox belongs to, matched on the normalized column rather than the raw one.
    /// </summary>
    [Alias(nameof(GetUserIdByEmailAsync))]
    Task<Guid?> GetUserIdByEmailAsync(string email, CancellationToken ct = default);

    [Alias(nameof(GetUserOperatorInfoAsync))]
    Task<OperatorBasicInfo?> GetUserOperatorInfoAsync(Guid userId, CancellationToken ct = default);

    [Alias(nameof(GetOperatorAppAccessAsync))]
    Task<OperatorAppAccessInfo?> GetOperatorAppAccessAsync(Guid operatorId, Guid appId, CancellationToken ct = default);

    /// <summary>
    /// Whether this operator has any per-app grant at all.
    /// </summary>
    /// <remarks>
    /// The answer decides which of two models applies, so it cannot be inferred from a single app's
    /// grant being absent: an operator with no records anywhere reaches every internal app, and an
    /// operator with even one record reaches only the apps they were granted. Without this call the
    /// first kind would be locked out of everything.
    /// </remarks>
    [Alias(nameof(GetOperatorHasAnyAppAccessAsync))]
    Task<bool> GetOperatorHasAnyAppAccessAsync(Guid operatorId, CancellationToken ct = default);
}
