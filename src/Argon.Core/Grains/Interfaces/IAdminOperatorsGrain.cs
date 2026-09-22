namespace Argon.Grains.Interfaces;

using ConsoleContracts;

/// <summary>
/// The outcome of a certificate revocation, with what the audit line names.
/// </summary>
public record AdminCertificateRevocation(OperatorActionResult Result, Guid? OperatorId, string? SerialNumber);

/// <summary>
/// The outcome of an app-access grant, with the application the audit line names.
/// </summary>
public record AdminAppAccessGrant(OperatorAppAccessResult Result, string? AppName, string? ClientId);

/// <summary>
/// Staff administration: operators, their certificates, their per-app access, and the audit log.
/// </summary>
/// <remarks>
/// <para>Stateless, keyed by <see cref="Guid.Empty"/>; see <see cref="IAdminUsersGrain"/> for why the
/// console's queries live in grains. Hosted on <c>core</c>, beside <c>OperatorAuthChallengeGrain</c>
/// and the Vault PKI client that signs and revokes staff certificates.</para>
///
/// <para><b>The caller is passed in, not read.</b> An operator's identity is an ambient context on
/// the console and does not cross a grain call, so every method that only a system operator may use
/// takes the calling operator's id and checks it against the database here — the same check the
/// console used to make, now next to the write it guards.</para>
/// </remarks>
[Alias("Argon.Grains.Interfaces.IAdminOperatorsGrain")]
public interface IAdminOperatorsGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetOperatorsAsync))]
    Task<OperatorList> GetOperatorsAsync(CancellationToken ct = default);

    /// <summary>One operator with their account and recent audit trail, or null when there is none.</summary>
    [Alias(nameof(GetOperatorDetailsAsync))]
    Task<OperatorDetails?> GetOperatorDetailsAsync(Guid operatorId, CancellationToken ct = default);

    [Alias(nameof(CreateOperatorAsync))]
    Task<CreateOperatorResult> CreateOperatorAsync(Guid callerOperatorId, CreateOperatorInput input, CancellationToken ct = default);

    [Alias(nameof(SetOperatorActiveAsync))]
    Task<OperatorActionResult> SetOperatorActiveAsync(Guid callerOperatorId, Guid operatorId, bool isActive,
        CancellationToken ct = default);

    // ── certificates ─────────────────────────────────────────────────────────────────────────

    [Alias(nameof(EnrollCertificateAsync))]
    Task<EnrollCertificateResult> EnrollCertificateAsync(Guid callerOperatorId, Guid operatorId, string csrPem,
        string? deviceName, string? deviceSerialNumber, CancellationToken ct = default);

    /// <summary>Revokes one certificate in Vault and marks the record; the record is kept as history.</summary>
    [Alias(nameof(RevokeCertificateAsync))]
    Task<AdminCertificateRevocation> RevokeCertificateAsync(Guid callerOperatorId, Guid certificateId, CancellationToken ct = default);

    // ── per-app access ───────────────────────────────────────────────────────────────────────

    [Alias(nameof(GetAppAccessAsync))]
    Task<OperatorAppAccessList> GetAppAccessAsync(Guid operatorId, CancellationToken ct = default);

    [Alias(nameof(GrantAppAccessAsync))]
    Task<AdminAppAccessGrant> GrantAppAccessAsync(Guid callerOperatorId, GrantOperatorAppAccessInput input,
        CancellationToken ct = default);

    [Alias(nameof(UpdateAppAccessAsync))]
    Task<OperatorAppAccessResult> UpdateAppAccessAsync(Guid callerOperatorId, UpdateOperatorAppAccessInput input,
        CancellationToken ct = default);

    [Alias(nameof(RevokeAppAccessAsync))]
    Task<OperatorActionResult> RevokeAppAccessAsync(Guid callerOperatorId, Guid operatorId, Guid appId,
        CancellationToken ct = default);

    // ── audit ────────────────────────────────────────────────────────────────────────────────

    [Alias(nameof(AppendAuditAsync))]
    Task AppendAuditAsync(Guid operatorId, string operatorEmail, string action, string? targetType, string? targetId,
        string? details, CancellationToken ct = default);

    [Alias(nameof(QueryAuditAsync))]
    Task<AuditLogPage> QueryAuditAsync(Guid? operatorId, string? action, string? targetId, DateTimeOffset? fromDate,
        DateTimeOffset? toDate, int page, int pageSize, CancellationToken ct = default);
}
