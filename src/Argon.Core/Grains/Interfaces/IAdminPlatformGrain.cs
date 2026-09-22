namespace Argon.Grains.Interfaces;

using ConsoleContracts;

/// <summary>
/// The outcome of a tenant edit, with the domain it touched for the audit line.
/// </summary>
public record AdminTenantChange(TenantActionResult Result, string? Domain);

/// <summary>
/// The operator console's platform-wide pages: statistics, the item and coupon catalogue, the tenant
/// directory, the database probe and the e-mail journal.
/// </summary>
/// <remarks>
/// Stateless, keyed by <see cref="Guid.Empty"/>; see <see cref="IAdminUsersGrain"/> for why the
/// console's queries live in grains. Hosted on <c>jobs</c>, beside the batch work: the statistics are
/// whole-table counts, and that is not load to put next to the hot path.
/// </remarks>
[Alias("Argon.Grains.Interfaces.IAdminPlatformGrain")]
public interface IAdminPlatformGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetPlatformStatsAsync))]
    Task<PlatformStats> GetPlatformStatsAsync(CancellationToken ct = default);

    /// <summary>A round trip to the database from the silo, timed.</summary>
    [Alias(nameof(PingDatabaseAsync))]
    Task<DatabaseDiagnostics> PingDatabaseAsync(CancellationToken ct = default);

    /// <summary>One page of what the platform mailed, newest first.</summary>
    /// <remarks>
    /// Read here because the journal is kept by the roles that send mail; the console only reads it,
    /// and would otherwise need the database the journal's writer resolves addresses against.
    /// </remarks>
    [Alias(nameof(ReadEmailJournalAsync))]
    Task<EmailJournalPage> ReadEmailJournalAsync(Guid? userId, int offset, int limit, CancellationToken ct = default);

    // ── items and coupons ────────────────────────────────────────────────────────────────────

    [Alias(nameof(GetItemTemplatesAsync))]
    Task<ItemTemplateList> GetItemTemplatesAsync(CancellationToken ct = default);

    [Alias(nameof(ReferenceItemExistsAsync))]
    Task<bool> ReferenceItemExistsAsync(Guid itemId, CancellationToken ct = default);

    [Alias(nameof(CreateItemTemplateAsync))]
    Task<CreateItemTemplateResult> CreateItemTemplateAsync(CreateItemTemplateInput input, CancellationToken ct = default);

    [Alias(nameof(DeleteItemTemplateAsync))]
    Task<DeleteItemResult> DeleteItemTemplateAsync(Guid itemId, CancellationToken ct = default);

    [Alias(nameof(DeleteItemFromUserInventoryAsync))]
    Task<DeleteItemResult> DeleteItemFromUserInventoryAsync(Guid userId, Guid itemId, CancellationToken ct = default);

    [Alias(nameof(GetCouponsAsync))]
    Task<CouponList> GetCouponsAsync(CancellationToken ct = default);

    [Alias(nameof(CreateCouponAsync))]
    Task<CreateCouponResult> CreateCouponAsync(CreateCouponInput input, CancellationToken ct = default);

    // ── tenant directory ─────────────────────────────────────────────────────────────────────

    [Alias(nameof(GetTenantDirectoryAsync))]
    Task<TenantDirectoryList> GetTenantDirectoryAsync(CancellationToken ct = default);

    /// <summary>Adds an unverified tenant; the domain and URL arrive already validated.</summary>
    [Alias(nameof(CreateTenantAsync))]
    Task<TenantActionResult> CreateTenantAsync(string domain, string instanceUrl, string? orgName, Guid? ownerUserId, string? notes,
        CancellationToken ct = default);

    [Alias(nameof(UpdateTenantAsync))]
    Task<AdminTenantChange> UpdateTenantAsync(Guid tenantId, string instanceUrl, string? orgName, string? notes,
        CancellationToken ct = default);

    /// <summary>Verifies a tenant, which only a system operator may do.</summary>
    [Alias(nameof(SetTenantVerifiedAsync))]
    Task<AdminTenantChange> SetTenantVerifiedAsync(Guid callerOperatorId, Guid tenantId, bool isVerified, CancellationToken ct = default);

    [Alias(nameof(DeleteTenantAsync))]
    Task<AdminTenantChange> DeleteTenantAsync(Guid tenantId, CancellationToken ct = default);
}
