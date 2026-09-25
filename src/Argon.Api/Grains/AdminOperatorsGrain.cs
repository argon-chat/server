namespace Argon.Grains;

using System.Security.Cryptography.X509Certificates;
using Argon.Features.Admin;
using Argon.Features.Vault;
using Argon.Grains.Interfaces;
using ConsoleContracts;
using ion.runtime;
using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;

/// <inheritdoc cref="IAdminOperatorsGrain"/>
[StatelessWorker]
public sealed class AdminOperatorsGrain(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IVaultPkiService pkiService,
    HybridCache cache,
    ILogger<AdminOperatorsGrain> logger) : Grain, IAdminOperatorsGrain
{
    public async Task<OperatorList> GetOperatorsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entities = await db.Operators
           .AsNoTracking()
           .Include(o => o.Certificates.Where(c => !c.IsDeleted))
           .Where(o => !o.IsDeleted)
           .OrderByDescending(o => o.CreatedAt)
           .ToListAsync(ct);

        var operators = entities.Select(MapOperatorInfo).ToList();

        return new OperatorList(new IonArray<OperatorInfo>(operators));
    }

    public async Task<OperatorDetails?> GetOperatorDetailsAsync(Guid operatorId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var op = await db.Operators
           .AsNoTracking()
           .Include(o => o.Certificates.Where(c => !c.IsDeleted))
           .Include(o => o.User)
           .ThenInclude(u => u!.Profile)
           .FirstOrDefaultAsync(o => o.Id == operatorId && !o.IsDeleted, ct);

        if (op is null)
            return null;

        var info = MapOperatorInfo(op);

        UserAccountInfo?  account = null;
        ArgonUserProfile? profile = null;
        if (op.User is { } user)
        {
            account = new UserAccountInfo(
                user.Id,
                user.Username,
                user.DisplayName,
                user.Email,
                user.PhoneNumber,
                user.AvatarFileId,
                user.DateOfBirth,
                user.CreatedAt.UtcDateTime,
                user.LockdownReason,
                user.LockDownExpiration?.UtcDateTime,
                user.LockDownIsAppealable,
                user.PreferredAuthMode,
                user.PreferredOtpMethod,
                user.AgreeTOS,
                null,
                0, 0, 0
            );
            profile = user.Profile?.ToDto();
        }

        var recentAudit = await db.OperatorAuditLog
           .AsNoTracking()
           .Where(a => a.OperatorId == operatorId)
           .OrderByDescending(a => a.CreatedAt)
           .Take(20)
           .ToListAsync(ct);

        return new OperatorDetails(info, account, profile, new IonArray<AuditEntry>(recentAudit.Select(MapAuditEntry).ToList()));
    }

    public async Task<CreateOperatorResult> CreateOperatorAsync(Guid callerOperatorId, CreateOperatorInput input,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsSystemOperatorAsync(db, callerOperatorId, ct))
            return new CreateOperatorResult(false, null, "Only system operators can create new operators");

        if (string.IsNullOrWhiteSpace(input.displayName) || string.IsNullOrWhiteSpace(input.email))
            return new CreateOperatorResult(false, null, "Display name and email are required");

        var email = input.email.Trim().ToLowerInvariant();

        var exists = await db.Operators.AnyAsync(o => o.Email == email && !o.IsDeleted, ct);
        if (exists)
            return new CreateOperatorResult(false, null, "An operator with this email already exists");

        if (input.userId.HasValue)
        {
            var userExists = await db.Users.AnyAsync(u => u.Id == input.userId.Value, ct);
            if (!userExists)
                return new CreateOperatorResult(false, null, "Linked user not found");

            var alreadyLinked = await db.Operators.AnyAsync(o => o.UserId == input.userId.Value && !o.IsDeleted, ct);
            if (alreadyLinked)
                return new CreateOperatorResult(false, null, "This user is already linked to another operator");
        }

        var entity = new OperatorEntity
        {
            DisplayName      = input.displayName.Trim(),
            Email            = email,
            UserId           = input.userId,
            IsActive         = true,
            IsSystemOperator = input.isSystemOperator
        };

        db.Operators.Add(entity);
        await db.SaveChangesAsync(ct);

        await InvalidateOperatorCacheAsync(entity.UserId, ct);

        logger.LogInformation("Operator={CallerId} created operator={OperatorId} email={Email}", callerOperatorId, entity.Id, entity.Email);

        return new CreateOperatorResult(true, entity.Id, null);
    }

    public async Task<OperatorActionResult> SetOperatorActiveAsync(Guid callerOperatorId, Guid operatorId, bool isActive,
        CancellationToken ct = default)
    {
        var verb = isActive ? "activate" : "deactivate";

        if (!isActive && callerOperatorId == operatorId)
            return new OperatorActionResult(false, "Cannot deactivate yourself");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsSystemOperatorAsync(db, callerOperatorId, ct))
            return new OperatorActionResult(false, $"Only system operators can {verb} other operators");

        var op = await db.Operators.FirstOrDefaultAsync(o => o.Id == operatorId && !o.IsDeleted, ct);
        if (op is null)
            return new OperatorActionResult(false, "Operator not found");

        if (op.IsActive == isActive)
            return new OperatorActionResult(false, isActive ? "Operator is already active" : "Operator is already inactive");

        op.IsActive = isActive;
        await db.SaveChangesAsync(ct);

        await InvalidateOperatorCacheAsync(op.UserId, ct);

        logger.LogInformation("Operator={CallerId} {Verb}d operator={OperatorId}", callerOperatorId, verb, operatorId);
        return new OperatorActionResult(true, null);
    }

    // ── certificates ─────────────────────────────────────────────────────────────────────────

    public async Task<EnrollCertificateResult> EnrollCertificateAsync(Guid callerOperatorId, Guid operatorId, string csrPem,
        string? deviceName, string? deviceSerialNumber, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsSystemOperatorAsync(db, callerOperatorId, ct))
            return Failed("Only system operators can enroll certificates");

        var op = await db.Operators.AsNoTracking().FirstOrDefaultAsync(x => x.Id == operatorId && !x.IsDeleted, ct);

        if (op is null)
            return Failed("Operator not found");
        if (!op.IsActive)
            return Failed("Operator is inactive");

        var csrValidation = CsrValidator.Validate(csrPem);
        if (!csrValidation.IsSuccess)
            return Failed(csrValidation.Error switch
            {
                CsrValidationError.KeyTooSmall => "CSR key size is too small (min RSA 2048, EC 256)",
                _                              => "Invalid CSR format"
            });

        SignedCertificateResult signed;
        try
        {
            signed = await pkiService.SignCsrAsync(csrPem, $"operator:{op.Email}");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Vault PKI signing failed for operator={OperatorId}", operatorId);
            return Failed("Vault PKI signing failed");
        }

        // parse signed cert to extract metadata
        var cert = X509Certificate2.CreateFromPem(signed.CertificatePem);

        // Add a new certificate record. Existing certificates remain active —
        // an operator may carry multiple certificates (one per device).
        var certificate = new OperatorCertificateEntity
        {
            OperatorId         = op.Id,
            SerialNumber       = signed.SerialNumber,
            Thumbprint         = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)),
            Subject            = cert.Subject,
            NotBefore          = cert.NotBefore.ToUniversalTime(),
            NotAfter           = cert.NotAfter.ToUniversalTime(),
            DeviceName         = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim(),
            DeviceSerialNumber = string.IsNullOrWhiteSpace(deviceSerialNumber) ? null : deviceSerialNumber.Trim(),
        };

        db.OperatorCertificates.Add(certificate);
        await db.SaveChangesAsync(ct);

        await InvalidateOperatorCacheAsync(op.UserId, ct);

        logger.LogInformation("Enrolled certificate {CertificateId} for operator={OperatorId}, serial={Serial}, device={Device}",
            certificate.Id, operatorId, signed.SerialNumber, certificate.DeviceSerialNumber);

        var caChain = string.Join("\n", signed.CaChainPem);
        if (string.IsNullOrEmpty(caChain))
            caChain = signed.IssuingCaPem;

        return new EnrollCertificateResult(
            true,
            certificate.Id,
            signed.CertificatePem,
            caChain,
            signed.SerialNumber,
            certificate.Thumbprint,
            new DateTimeOffset(cert.NotAfter).UtcDateTime,
            null);

        static EnrollCertificateResult Failed(string error)
            => new(false, null, null, null, null, null, null, error);
    }

    public async Task<AdminCertificateRevocation> RevokeCertificateAsync(Guid callerOperatorId, Guid certificateId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var cert = await db.OperatorCertificates.FirstOrDefaultAsync(c => c.Id == certificateId && !c.IsDeleted, ct);
        if (cert is null)
            return Refused("Certificate not found");

        if (callerOperatorId == cert.OperatorId)
            return Refused("Cannot revoke your own certificate");

        if (!await IsSystemOperatorAsync(db, callerOperatorId, ct))
            return Refused("Only system operators can revoke certificates");

        // Soft-revoke: the record is preserved as history. Revoking twice is not an error.
        if (cert.RevokedAt is null)
        {
            await pkiService.RevokeCertificateAsync(cert.SerialNumber);

            cert.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var userId = await db.Operators.Where(o => o.Id == cert.OperatorId).Select(o => o.UserId).FirstOrDefaultAsync(ct);
            await InvalidateOperatorCacheAsync(userId, ct);

            logger.LogInformation("Operator={CallerId} revoked certificate={CertificateId} (serial={Serial}) for operator={OperatorId}",
                callerOperatorId, cert.Id, cert.SerialNumber, cert.OperatorId);
        }

        return new AdminCertificateRevocation(new OperatorActionResult(true, null), cert.OperatorId, cert.SerialNumber);

        static AdminCertificateRevocation Refused(string error)
            => new(new OperatorActionResult(false, error), null, null);
    }

    // ── per-app access ───────────────────────────────────────────────────────────────────────

    public async Task<OperatorAppAccessList> GetAppAccessAsync(Guid operatorId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var raw = await db.OperatorAppAccess
           .AsNoTracking()
           .Where(a => a.OperatorId == operatorId)
           .Join(db.AppEntities.AsNoTracking(),
                a => a.AppId,
                app => app.AppId,
                (a, app) => new
                {
                    a.OperatorId,
                    a.AppId,
                    AppName = app.Name,
                    app.ClientId,
                    a.AllowedScopes,
                    a.Claims,
                    a.GrantedBy,
                    a.GrantedAt,
                    a.IsActive
                })
           .ToListAsync(ct);

        var records = raw.Select(r => new OperatorAppAccessEntry(
            r.OperatorId,
            r.AppId,
            r.AppName,
            r.ClientId,
            new IonArray<string>(r.AllowedScopes),
            new IonArray<string>(r.Claims),
            r.GrantedBy,
            r.GrantedAt.UtcDateTime,
            r.IsActive
        )).ToList();

        return new OperatorAppAccessList(new IonArray<OperatorAppAccessEntry>(records));
    }

    public async Task<AdminAppAccessGrant> GrantAppAccessAsync(Guid callerOperatorId, GrantOperatorAppAccessInput input,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsSystemOperatorAsync(db, callerOperatorId, ct))
            return Refused("Only system operators can manage operator app access");

        var operatorExists = await db.Operators.AnyAsync(o => o.Id == input.operatorId && !o.IsDeleted, ct);
        if (!operatorExists)
            return Refused("Operator not found");

        var app = await db.AppEntities.AsNoTracking().FirstOrDefaultAsync(a => a.AppId == input.appId, ct);
        if (app is null)
            return Refused("Application not found");

        var existing = await db.OperatorAppAccess
           .AsNoTracking()
           .FirstOrDefaultAsync(a => a.OperatorId == input.operatorId && a.AppId == input.appId, ct);

        if (existing is not null)
            return Refused("Access record already exists. Use UpdateOperatorAppAccess to modify it.");

        db.OperatorAppAccess.Add(new OperatorAppAccessEntity
        {
            OperatorId    = input.operatorId,
            AppId         = input.appId,
            AllowedScopes = input.allowedScopes.Values.ToList(),
            Claims        = input.claims.Values.ToList(),
            GrantedBy     = callerOperatorId,
            GrantedAt     = DateTimeOffset.UtcNow,
            IsActive      = true
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Operator={CallerId} granted app access: operator={OperatorId} app={AppId} ({AppName})",
            callerOperatorId, input.operatorId, input.appId, app.Name);

        await InvalidateAppAccessCacheAsync(input.operatorId, input.appId, ct);

        return new AdminAppAccessGrant(new OperatorAppAccessResult(true, null), app.Name, app.ClientId);

        static AdminAppAccessGrant Refused(string error)
            => new(new OperatorAppAccessResult(false, error), null, null);
    }

    public async Task<OperatorAppAccessResult> UpdateAppAccessAsync(Guid callerOperatorId, UpdateOperatorAppAccessInput input,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsSystemOperatorAsync(db, callerOperatorId, ct))
            return new OperatorAppAccessResult(false, "Only system operators can manage operator app access");

        var record = await db.OperatorAppAccess
           .FirstOrDefaultAsync(a => a.OperatorId == input.operatorId && a.AppId == input.appId, ct);

        if (record is null)
            return new OperatorAppAccessResult(false, "Access record not found");

        record.AllowedScopes = input.allowedScopes.Values.ToList();
        record.Claims        = input.claims.Values.ToList();
        record.IsActive      = input.isActive;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Operator={CallerId} updated app access: operator={OperatorId} app={AppId} active={IsActive}",
            callerOperatorId, input.operatorId, input.appId, input.isActive);

        await InvalidateAppAccessCacheAsync(input.operatorId, input.appId, ct);

        return new OperatorAppAccessResult(true, null);
    }

    public async Task<OperatorActionResult> RevokeAppAccessAsync(Guid callerOperatorId, Guid operatorId, Guid appId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsSystemOperatorAsync(db, callerOperatorId, ct))
            return new OperatorActionResult(false, "Only system operators can manage operator app access");

        var record = await db.OperatorAppAccess
           .FirstOrDefaultAsync(a => a.OperatorId == operatorId && a.AppId == appId, ct);

        if (record is null)
            return new OperatorActionResult(false, "Access record not found");

        db.OperatorAppAccess.Remove(record);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Operator={CallerId} revoked app access: operator={OperatorId} app={AppId}",
            callerOperatorId, operatorId, appId);

        await InvalidateAppAccessCacheAsync(operatorId, appId, ct);

        return new OperatorActionResult(true, null);
    }

    // ── audit ────────────────────────────────────────────────────────────────────────────────

    public async Task AppendAuditAsync(Guid operatorId, string operatorEmail, string action, string? targetType, string? targetId,
        string? details, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        db.OperatorAuditLog.Add(new OperatorAuditEntity
        {
            OperatorId    = operatorId,
            OperatorEmail = operatorEmail,
            Action        = action,
            TargetType    = targetType,
            TargetId      = targetId,
            Details       = details
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task<AuditLogPage> QueryAuditAsync(Guid? operatorId, string? action, string? targetId, DateTimeOffset? fromDate,
        DateTimeOffset? toDate, int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.OperatorAuditLog.AsNoTracking();

        if (operatorId.HasValue)
            query = query.Where(a => a.OperatorId == operatorId.Value);
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(targetId))
            query = query.Where(a => a.TargetId == targetId);
        if (fromDate.HasValue)
            query = query.Where(a => a.CreatedAt >= fromDate.Value);
        if (toDate.HasValue)
            query = query.Where(a => a.CreatedAt <= toDate.Value);

        var totalCount = await query.CountAsync(ct);

        var entries = await query
           .OrderByDescending(a => a.CreatedAt)
           .Skip(page * pageSize)
           .Take(pageSize)
           .ToListAsync(ct);

        return new AuditLogPage(new IonArray<AuditEntry>(entries.Select(MapAuditEntry).ToList()), totalCount, page, pageSize);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the caller may manage other operators. Asked of the database, not of the token: a
    /// system operator demoted a minute ago still holds a token that says otherwise.
    /// </summary>
    private static Task<bool> IsSystemOperatorAsync(ApplicationDbContext db, Guid operatorId, CancellationToken ct)
        => db.Operators.AnyAsync(o => o.Id == operatorId && !o.IsDeleted && o.IsSystemOperator, ct);

    /// <summary>
    /// Invalidate Aegis-side HybridCache entries for operator app access.
    /// Keys match AegisDirectory; clears Redis L2 immediately,
    /// L1 on Aegis side expires within LocalCacheExpiration (1 min).
    /// </summary>
    private async Task InvalidateAppAccessCacheAsync(Guid operatorId, Guid appId, CancellationToken ct)
    {
        await cache.RemoveAsync($"aegis:operator:app-access:{operatorId}:{appId}", ct);
        await cache.RemoveAsync($"aegis:operator:has-app-access:{operatorId}", ct);
    }

    /// <summary>Drops Aegis's cached operator record (active, live certificate) for the linked account.</summary>
    private async Task InvalidateOperatorCacheAsync(Guid? userId, CancellationToken ct)
    {
        if (userId is { } id)
            await cache.RemoveAsync($"aegis:user:operator:{id}", ct);
    }

    private static OperatorInfo MapOperatorInfo(OperatorEntity op)
    {
        var certificates = (op.Certificates ?? new List<OperatorCertificateEntity>())
           .OrderByDescending(c => c.CreatedAt)
           .Select(MapOperatorCertificateInfo)
           .ToList();

        return new OperatorInfo(
            op.Id,
            op.DisplayName,
            op.Email,
            op.UserId,
            op.IsActive,
            op.IsSystemOperator,
            new IonArray<OperatorCertificateInfo>(certificates),
            op.LastAuthAt?.UtcDateTime,
            op.CreatedAt.UtcDateTime
        );
    }

    private static OperatorCertificateInfo MapOperatorCertificateInfo(OperatorCertificateEntity c)
        => new(
            c.Id,
            c.SerialNumber,
            c.Thumbprint,
            c.Subject,
            c.NotBefore.UtcDateTime,
            c.NotAfter.UtcDateTime,
            c.NotAfter < DateTimeOffset.UtcNow,
            c.RevokedAt.HasValue,
            c.CreatedAt.UtcDateTime,
            c.DeviceName,
            c.DeviceSerialNumber
        );

    private static AuditEntry MapAuditEntry(OperatorAuditEntity a)
        => new(
            a.Id,
            a.OperatorId,
            a.OperatorEmail,
            a.Action,
            a.TargetType,
            a.TargetId,
            a.Details,
            a.CreatedAt.UtcDateTime
        );
}
