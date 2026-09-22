namespace Argon.Grains.Interfaces;

using ConsoleContracts;

/// <summary>
/// What the trust card shows about an account beside the score the trust grain computes. The account
/// fields are null when there is no such account.
/// </summary>
public record AdminTrustFacts(string? Username, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, int AutoActionsApplied);

/// <summary>
/// The display fields of an account a deletion page names — erased ones included, so any of them may
/// be blank.
/// </summary>
public record AdminAccountIdentity(Guid Id, string Username, string DisplayName, string Email);

/// <summary>
/// The operator console's reads and writes on accounts: search, the user card, account fields,
/// devices, payments, and the facts behind the trust and deletion pages.
/// </summary>
/// <remarks>
/// Stateless, keyed by <see cref="Guid.Empty"/>. It exists as a grain rather than as queries in the
/// console because database access lives in grains — <c>admin</c> is a client role and opens no
/// connection of its own, exactly like <c>account</c> and <c>aegis</c>.
/// <para>
/// The returned types are the console's own Ion contracts, which cross the grain boundary intact, so
/// the console maps nothing twice. Auditing stays with the console: the operator's identity is an
/// ambient context there and does not cross a grain call.
/// </para>
/// </remarks>
[Alias("Argon.Grains.Interfaces.IAdminUsersGrain")]
public interface IAdminUsersGrain : IGrainWithGuidKey
{
    [Alias(nameof(SearchUserAsync))]
    Task<SearchUserResult> SearchUserAsync(string query, CancellationToken ct = default);

    /// <summary>The user card, or null when there is no such account.</summary>
    [Alias(nameof(GetUserCardAsync))]
    Task<UserCardDetails?> GetUserCardAsync(Guid userId, CancellationToken ct = default);

    [Alias(nameof(UserExistsAsync))]
    Task<bool> UserExistsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Sets or clears a lockdown, and drops the cached copy the request pipeline checks it against.
    /// </summary>
    [Alias(nameof(SetLockdownAsync))]
    Task<UserActionResult> SetLockdownAsync(Guid userId, LockdownReason reason, DateTimeOffset? expiration, bool isAppealable,
        CancellationToken ct = default);

    [Alias(nameof(ChangeUsernameAsync))]
    Task<UserActionResult> ChangeUsernameAsync(Guid userId, string newUsername, CancellationToken ct = default);

    [Alias(nameof(ChangeEmailAsync))]
    Task<UserActionResult> ChangeEmailAsync(Guid userId, string newEmail, CancellationToken ct = default);

    [Alias(nameof(RemoveTwoFactorAsync))]
    Task<UserActionResult> RemoveTwoFactorAsync(Guid userId, CancellationToken ct = default);

    [Alias(nameof(RemovePhoneNumberAsync))]
    Task<UserActionResult> RemovePhoneNumberAsync(Guid userId, CancellationToken ct = default);

    [Alias(nameof(ChangeAuthModeAsync))]
    Task<UserActionResult> ChangeAuthModeAsync(Guid userId, ArgonAuthMode authMode, CancellationToken ct = default);

    [Alias(nameof(ChangeOtpMethodAsync))]
    Task<UserActionResult> ChangeOtpMethodAsync(Guid userId, OtpMethod otpMethod, CancellationToken ct = default);

    // ── devices ──────────────────────────────────────────────────────────────────────────────

    [Alias(nameof(GetUserDevicesAsync))]
    Task<DeviceList> GetUserDevicesAsync(Guid userId, CancellationToken ct = default);

    [Alias(nameof(GetDeviceAccountsAsync))]
    Task<DeviceAccountList> GetDeviceAccountsAsync(Guid deviceId, CancellationToken ct = default);

    [Alias(nameof(BanDeviceAsync))]
    Task<UserActionResult> BanDeviceAsync(Guid deviceId, string reason, DateTimeOffset? expiration, CancellationToken ct = default);

    [Alias(nameof(UnbanDeviceAsync))]
    Task<UserActionResult> UnbanDeviceAsync(Guid deviceId, CancellationToken ct = default);

    // ── payments ─────────────────────────────────────────────────────────────────────────────

    [Alias(nameof(GetUserTransactionsAsync))]
    Task<AdminTransactionPage> GetUserTransactionsAsync(Guid userId, int page, int pageSize, CancellationToken ct = default);

    [Alias(nameof(GetTransactionByXsollaIdAsync))]
    Task<AdminTransactionDetails?> GetTransactionByXsollaIdAsync(string xsollaTxId, CancellationToken ct = default);

    // ── trust and deletion ───────────────────────────────────────────────────────────────────

    /// <summary>The account-side half of the trust card.</summary>
    [Alias(nameof(GetTrustFactsAsync))]
    Task<AdminTrustFacts> GetTrustFactsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The display fields of the accounts a deletion page names, read past the soft-delete filter.
    /// </summary>
    /// <remarks>
    /// An erasure sets the very flag the global filter hides, so an account whose deletion is under way
    /// would otherwise vanish from the page that exists to show it.
    /// </remarks>
    [Alias(nameof(GetAccountIdentitiesAsync))]
    Task<Dictionary<Guid, AdminAccountIdentity>> GetAccountIdentitiesAsync(List<Guid> ids, CancellationToken ct = default);

    /// <summary>What erasing one account would actually destroy, and what would refuse it now.</summary>
    /// <remarks>
    /// Here rather than in the console because it is the sweep's own arithmetic — the threshold
    /// options and the deletion grain's status both live on the role that hosts this grain.
    /// </remarks>
    [Alias(nameof(GetAccountDeletionImpactAsync))]
    Task<AccountDeletionImpact> GetAccountDeletionImpactAsync(Guid userId, CancellationToken ct = default);
}
