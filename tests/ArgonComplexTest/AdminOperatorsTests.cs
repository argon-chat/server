namespace ArgonComplexTest.Tests;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Argon.Features.Aegis;
using ConsoleContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Staff administration through the console: operators, their certificates, their per-app access,
/// and the audit log that records all of it.
/// </summary>
/// <remarks>
/// <para>Everything here is gated on the caller being a <em>system</em> operator, checked against the
/// database next to the write, so the refusals are tested as carefully as the successes.</para>
///
/// <para>The identity server reads operators and their app grants through a cache
/// (<see cref="AegisDirectory"/>). A write the console makes is only as good as that cache's view of
/// it, so the tests read back through a real <see cref="AegisDirectory"/> built on the host's own
/// <see cref="HybridCache"/> — the object the <c>aegis</c> role would hold — rather than through the
/// database the cache sits in front of.</para>
/// </remarks>
[TestFixture]
public class AdminOperatorsTests : AdminTestBase
{
    protected override Guid SystemOperatorId  => Guid.Parse("00000000-0000-0000-0000-0000000ad101");
    protected override Guid RegularOperatorId => Guid.Parse("00000000-0000-0000-0000-0000000ad102");

    private FakeVaultPkiService Vault => FactoryAsp.Services.GetRequiredService<FakeVaultPkiService>();

    private IAegisDirectory Aegis()
        => new AegisDirectory(GetGrainFactory(), FactoryAsp.Services.GetRequiredService<HybridCache>());

    private static string NewOperatorEmail(string prefix = "op") => $"{prefix}_{Guid.NewGuid():N}@argon.test";

    private static string EcCsr()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=operator device", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
    }

    private static async Task<Guid> CreateOperatorAsync(IAdminConsole admin, string email, Guid? userId, CancellationToken ct)
    {
        var created = await admin.CreateOperator(new CreateOperatorInput("Coverage Operator", email, userId, false), ct);

        Assert.That(created.success, Is.True, created.error);

        return created.operatorId!.Value;
    }

    // ── Who may manage operators ────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_regular_operator_can_manage_neither_operators_nor_their_certificates_nor_their_access(
        CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var target        = await CreateOperatorAsync(admin, NewOperatorEmail(), null, ct);
        var enrolled      = await admin.EnrollOperatorCertificate(target, EcCsr(), null, null, ct);
        var teamOwner     = await CreateSessionAsync(ct);
        var (appId, _)    = await SeedAppAsync(await SeedTeamAsync(teamOwner.UserId, "Staff Team", ct), "Staff Tool", true, ct);
        var strangerEmail = NewOperatorEmail("refused");

        Assert.That(enrolled.success, Is.True, enrolled.error);

        var (regularScope, regular) = AdminAs(RegularOperatorId, RegularOperatorEmail);
        await using var __ = regularScope;

        var created       = await regular.CreateOperator(new CreateOperatorInput("Nope", strangerEmail, null, true), ct);
        var deactivated   = await regular.DeactivateOperator(target, ct);
        var activated     = await regular.ActivateOperator(target, ct);
        var enrolledAgain = await regular.EnrollOperatorCertificate(target, EcCsr(), null, null, ct);
        var revoked       = await regular.RevokeOperatorCertificate(enrolled.certificateId!.Value, ct);
        var granted       = await regular.GrantOperatorAppAccess(new GrantOperatorAppAccessInput(target, appId, new([]), new([])), ct);
        var updated       = await regular.UpdateOperatorAppAccess(
            new UpdateOperatorAppAccessInput(target, appId, new([]), new([]), false), ct);
        var revokedAccess = await regular.RevokeOperatorAppAccess(target, appId, ct);

        await using var db = await NewDbAsync(ct);

        var operatorRow = await db.Operators.AsNoTracking().SingleAsync(o => o.Id == target, ct);
        var certificate = await db.OperatorCertificates.AsNoTracking().SingleAsync(c => c.Id == enrolled.certificateId, ct);

        Assert.Multiple(async () =>
        {
            foreach (var (name, success, error) in new[]
                     {
                         ("create", created.success, created.error),
                         ("deactivate", deactivated.success, deactivated.error),
                         ("activate", activated.success, activated.error),
                         ("enroll", enrolledAgain.success, enrolledAgain.error),
                         ("revoke certificate", revoked.success, revoked.error),
                         ("grant access", granted.success, granted.error),
                         ("update access", updated.success, updated.error),
                         ("revoke access", revokedAccess.success, revokedAccess.error)
                     })
            {
                Assert.That(success, Is.False, $"a regular operator was allowed to {name}");
                Assert.That(error, Does.StartWith("Only system operators"), $"{name} was refused for the wrong reason");
            }

            Assert.That(await db.Operators.AnyAsync(o => o.Email == strangerEmail, ct), Is.False);
            Assert.That(operatorRow.IsActive, Is.True);
            Assert.That(certificate.RevokedAt, Is.Null);
            Assert.That(Vault.Revocations, Does.Not.Contain(certificate.SerialNumber));
            Assert.That(await db.OperatorAppAccess.AnyAsync(a => a.OperatorId == target, ct), Is.False);
            Assert.That(await db.OperatorCertificates.CountAsync(c => c.OperatorId == target, ct), Is.EqualTo(1),
                "the refused enrolment still stored a certificate");
        });
    }

    // ── Creating operators ──────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task CreateOperator_refuses_what_it_cannot_create(CancellationToken ct = default)
    {
        var linked = await CreateSessionAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        await CreateOperatorAsync(admin, NewOperatorEmail(), linked.UserId, ct);

        var noName         = await admin.CreateOperator(new CreateOperatorInput("  ", NewOperatorEmail(), null, false), ct);
        var noEmail        = await admin.CreateOperator(new CreateOperatorInput("Name", " ", null, false), ct);
        var unknownUser    = await admin.CreateOperator(new CreateOperatorInput("Name", NewOperatorEmail(), Guid.NewGuid(), false), ct);
        var alreadyLinked  = await admin.CreateOperator(new CreateOperatorInput("Name", NewOperatorEmail(), linked.UserId, false), ct);

        Assert.Multiple(() =>
        {
            Assert.That(noName.success, Is.False);
            Assert.That(noName.error, Is.EqualTo("Display name and email are required"));
            Assert.That(noEmail.success, Is.False);
            Assert.That(noEmail.error, Is.EqualTo("Display name and email are required"));
            Assert.That(unknownUser.success, Is.False);
            Assert.That(unknownUser.error, Is.EqualTo("Linked user not found"));
            Assert.That(alreadyLinked.success, Is.False);
            Assert.That(alreadyLinked.error, Is.EqualTo("This user is already linked to another operator"));
        });
    }

    /// <summary>
    /// An address is stored trimmed and lower-cased, so the duplicate check has to compare it that way.
    /// </summary>
    /// <remarks>
    /// The check used to compare the address as typed against the stored, normalised one. The same
    /// address in another case therefore passed the check and hit the unique index instead, and the
    /// operator was told "Failed to create operator" — with an error logged — rather than why.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task CreateOperator_sees_the_same_address_in_another_case_as_a_duplicate(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var email = NewOperatorEmail("Mixed.Case");

        var first  = await admin.CreateOperator(new CreateOperatorInput("First", email, null, false), ct);
        var second = await admin.CreateOperator(new CreateOperatorInput("Second", $"  {email.ToUpperInvariant()} ", null, false), ct);

        await using var db = await NewDbAsync(ct);
        var stored = await db.Operators.AsNoTracking().SingleAsync(o => o.Id == first.operatorId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(first.success, Is.True, first.error);
            Assert.That(stored.Email, Is.EqualTo(email.ToLowerInvariant()), "the address is stored normalised");
            Assert.That(second.success, Is.False);
            Assert.That(second.error, Is.EqualTo("An operator with this email already exists"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task An_operator_linked_to_an_account_shows_the_account_and_its_own_audit_trail(CancellationToken ct = default)
    {
        var person  = await CreateSessionAsync(ct);
        var subject = await CreateSessionAsync(ct);
        var email   = NewOperatorEmail("linked");

        var (scope, admin) = Admin();
        await using var _ = scope;

        var operatorId = await CreateOperatorAsync(admin, email, person.UserId, ct);

        var (asNewScope, asNew) = AdminAs(operatorId, email);
        await using var __ = asNewScope;

        Assert.That((await asNew.GrantXp(subject.UserId, 5, ct)).success, Is.True);

        var (backScope, back) = Admin();
        await using var ___ = backScope;

        var details   = await back.GetOperatorDetails(operatorId, ct);
        var operators = await back.GetOperators(ct);

        Assert.Multiple(() =>
        {
            Assert.That(details.info.userId, Is.EqualTo(person.UserId));
            Assert.That(details.info.isSystemOperator, Is.False);
            Assert.That(details.linkedUser?.userId, Is.EqualTo(person.UserId));
            Assert.That(details.linkedUser?.username, Is.EqualTo(person.Credentials.username));
            Assert.That(details.linkedUser?.email, Is.EqualTo(person.Credentials.email));
            Assert.That(details.linkedProfile, Is.Not.Null);
            Assert.That(details.recentAuditEntries.Values.Select(e => (e.operatorId, e.action, e.targetId)),
                Does.Contain((operatorId, "GrantXp", subject.UserId.ToString())),
                "the details page is the operator's own trail, and this one granted XP");
            Assert.That(details.recentAuditEntries.Values.All(e => e.operatorId == operatorId), Is.True);
            Assert.That(operators.operators.Values.Select(o => o.operatorId), Does.Contain(operatorId));
        });
    }

    // ── Activating and deactivating ─────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task SetOperatorActive_refuses_a_no_op_an_unknown_operator_and_deactivating_yourself(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var target = await CreateOperatorAsync(admin, NewOperatorEmail(), null, ct);

        var yourself      = await admin.DeactivateOperator(SystemOperatorId, ct);
        var unknown       = await admin.ActivateOperator(Guid.NewGuid(), ct);
        var alreadyActive = await admin.ActivateOperator(target, ct);
        var deactivated   = await admin.DeactivateOperator(target, ct);
        var twice         = await admin.DeactivateOperator(target, ct);

        var audit = await AuditAsync(admin, "DeactivateOperator", target.ToString(), ct);

        Assert.Multiple(() =>
        {
            Assert.That(yourself.success, Is.False);
            Assert.That(yourself.error, Is.EqualTo("Cannot deactivate yourself"));
            Assert.That(unknown.success, Is.False);
            Assert.That(unknown.error, Is.EqualTo("Operator not found"));
            Assert.That(alreadyActive.success, Is.False);
            Assert.That(alreadyActive.error, Is.EqualTo("Operator is already active"));
            Assert.That(deactivated.success, Is.True, deactivated.error);
            Assert.That(twice.success, Is.False);
            Assert.That(twice.error, Is.EqualTo("Operator is already inactive"));
            Assert.That(audit, Has.Count.EqualTo(1), "one deactivation happened, and only it is on the record");
        });
    }

    /// <summary>
    /// The identity server learns of a new, deactivated or reactivated operator on its next sign-in.
    /// </summary>
    /// <remarks>
    /// <para>Aegis reads an account's operator record through <see cref="AegisDirectory.GetOperatorAsync"/>,
    /// cached for two minutes, and refuses an inactive operator from that cached copy. Nothing dropped
    /// it: the console's app-access writes did, the operator writes did not. So an operator deactivated
    /// because their key was lost kept signing into staff tools for up to two minutes afterwards, and a
    /// freshly created one was told "no operator record found" for as long.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Operator_changes_reach_the_identity_server_at_once(CancellationToken ct = default)
    {
        var person = await CreateSessionAsync(ct);
        var aegis  = Aegis();

        var (scope, admin) = Admin();
        await using var _ = scope;

        var beforeCreation = await aegis.GetOperatorAsync(person.UserId, ct);

        var operatorId = await CreateOperatorAsync(admin, NewOperatorEmail("aegis"), person.UserId, ct);
        var created    = await aegis.GetOperatorAsync(person.UserId, ct);

        Assert.That((await admin.DeactivateOperator(operatorId, ct)).success, Is.True);
        var deactivated = await aegis.GetOperatorAsync(person.UserId, ct);

        Assert.That((await admin.ActivateOperator(operatorId, ct)).success, Is.True);
        var reactivated = await aegis.GetOperatorAsync(person.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(beforeCreation, Is.Null, "premise: the account was no operator, and Aegis cached that");
            Assert.That(created?.OperatorId, Is.EqualTo(operatorId), "Aegis still holds the account's old 'no operator' answer");
            Assert.That(deactivated?.IsActive, Is.False, "a deactivated operator still reads as active to the identity server");
            Assert.That(reactivated?.IsActive, Is.True, "a reactivated operator still reads as inactive to the identity server");
        });
    }

    // ── Certificates ────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task EnrollCertificate_signs_the_request_and_records_the_device_it_came_from(CancellationToken ct = default)
    {
        var person = await CreateSessionAsync(ct);
        var email  = NewOperatorEmail("enrol");
        var aegis  = Aegis();

        var (scope, admin) = Admin();
        await using var _ = scope;

        var operatorId = await CreateOperatorAsync(admin, email, person.UserId, ct);
        var before     = await aegis.GetOperatorAsync(person.UserId, ct);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=yubikey", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();

        var enrolled = await admin.EnrollOperatorCertificate(operatorId, csr, "  YubiKey 5 NFC ", " 19283746 ", ct);

        Assert.That(enrolled.success, Is.True, enrolled.error);

        var second = await admin.EnrollOperatorCertificate(operatorId, EcCsr(), "   ", null, ct);

        using var issued = X509Certificate2.CreateFromPem(enrolled.certificatePem!);
        var notAfter = new DateTimeOffset(issued.NotAfter);

        var after        = await aegis.GetOperatorAsync(person.UserId, ct);
        var details      = await admin.GetOperatorDetails(operatorId, ct);
        var certificates = details.info.certificates.Values.ToDictionary(c => c.certificateId);
        var first        = certificates.GetValueOrDefault(enrolled.certificateId!.Value);
        var audit        = await AuditAsync(admin, "EnrollOperatorCertificate", operatorId.ToString(), ct);

        Assert.Multiple(() =>
        {
            Assert.That(issued.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo(), Is.EqualTo(key.ExportSubjectPublicKeyInfo()),
                "the certificate handed back does not certify the key the operator asked for");
            Assert.That(enrolled.thumbprint, Is.EqualTo(Convert.ToHexString(issued.GetCertHash(HashAlgorithmName.SHA256))));
            Assert.That(enrolled.serialNumber, Is.Not.Null);
            Assert.That(Vault.Signed.GetValueOrDefault(enrolled.serialNumber!), Is.EqualTo($"operator:{email}"),
                "Vault is asked to sign for the operator's address");
            Assert.That(enrolled.caChainPem, Is.EqualTo(Vault.CaPem),
                "with no chain from Vault, the issuing CA is what the client needs to build one");
            Assert.That(enrolled.notAfter, Is.EqualTo(notAfter).Within(TimeSpan.FromSeconds(1)));

            Assert.That(second.success, Is.True, "an operator may carry one certificate per device");
            Assert.That(certificates, Has.Count.EqualTo(2));
            Assert.That(first, Is.Not.Null);
            Assert.That(first?.deviceName, Is.EqualTo("YubiKey 5 NFC"));
            Assert.That(first?.deviceSerialNumber, Is.EqualTo("19283746"));
            Assert.That(first?.serialNumber, Is.EqualTo(enrolled.serialNumber));
            Assert.That(first?.subject, Is.EqualTo(issued.Subject));
            Assert.That(first?.notAfter, Is.EqualTo(notAfter).Within(TimeSpan.FromSeconds(1)));
            Assert.That(first?.isRevoked, Is.False);
            Assert.That(first?.isExpired, Is.False);
            Assert.That(certificates.GetValueOrDefault(second.certificateId ?? Guid.Empty)?.deviceName, Is.Null,
                "a blank device name is no device name");

            Assert.That(before?.HasActiveCertificate, Is.False, "premise");
            Assert.That(after?.HasActiveCertificate, Is.True, "the identity server has not heard of the new certificate");

            Assert.That(audit, Has.Count.EqualTo(2));
            Assert.That(audit.Select(a => a.details), Has.Some.Contain(enrolled.serialNumber!));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task EnrollCertificate_refuses_a_bad_request_and_survives_Vault_being_down(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var active      = await CreateOperatorAsync(admin, NewOperatorEmail(), null, ct);
        var inactive    = await CreateOperatorAsync(admin, NewOperatorEmail(), null, ct);
        var unreachable = await CreateOperatorAsync(admin, NewOperatorEmail(FakeVaultPkiService.UnreachableMarker), null, ct);

        Assert.That((await admin.DeactivateOperator(inactive, ct)).success, Is.True);

        using var weakKey = RSA.Create(1024);
        var weakCsr = new CertificateRequest("CN=weak", weakKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
           .CreateSigningRequestPem();

        var unknown    = await admin.EnrollOperatorCertificate(Guid.NewGuid(), EcCsr(), null, null, ct);
        var disabled   = await admin.EnrollOperatorCertificate(inactive, EcCsr(), null, null, ct);
        var garbage    = await admin.EnrollOperatorCertificate(active, "-----BEGIN CERTIFICATE REQUEST-----\nnope\n-----END CERTIFICATE REQUEST-----", null, null, ct);
        var weak       = await admin.EnrollOperatorCertificate(active, weakCsr, null, null, ct);
        var vaultDown  = await admin.EnrollOperatorCertificate(unreachable, EcCsr(), null, null, ct);

        await using var db = await NewDbAsync(ct);
        var stored = await db.OperatorCertificates.CountAsync(c => c.OperatorId == active || c.OperatorId == unreachable, ct);

        Assert.Multiple(() =>
        {
            Assert.That((unknown.success, unknown.error), Is.EqualTo((false, "Operator not found")));
            Assert.That((disabled.success, disabled.error), Is.EqualTo((false, "Operator is inactive")));
            Assert.That((garbage.success, garbage.error), Is.EqualTo((false, "Invalid CSR format")));
            Assert.That((weak.success, weak.error), Is.EqualTo((false, "CSR key size is too small (min RSA 2048, EC 256)")));
            Assert.That((vaultDown.success, vaultDown.error), Is.EqualTo((false, "Vault PKI signing failed")));
            Assert.That(vaultDown.certificatePem, Is.Null);
            Assert.That(stored, Is.EqualTo(0), "a refused enrolment stored a certificate");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RevokeCertificate_keeps_the_record_tells_Vault_once_and_refuses_your_own(CancellationToken ct = default)
    {
        var person = await CreateSessionAsync(ct);
        var aegis  = Aegis();

        var (scope, admin) = Admin();
        await using var _ = scope;

        var operatorId = await CreateOperatorAsync(admin, NewOperatorEmail("revoke"), person.UserId, ct);
        var enrolled   = await admin.EnrollOperatorCertificate(operatorId, EcCsr(), "laptop", null, ct);
        var own        = await admin.EnrollOperatorCertificate(SystemOperatorId, EcCsr(), "mine", null, ct);

        Assert.That(enrolled.success && own.success, Is.True, $"{enrolled.error} {own.error}");

        var before  = await aegis.GetOperatorAsync(person.UserId, ct);
        var revoked = await admin.RevokeOperatorCertificate(enrolled.certificateId!.Value, ct);
        var after   = await aegis.GetOperatorAsync(person.UserId, ct);
        var again   = await admin.RevokeOperatorCertificate(enrolled.certificateId!.Value, ct);
        var unknown = await admin.RevokeOperatorCertificate(Guid.NewGuid(), ct);
        var mine    = await admin.RevokeOperatorCertificate(own.certificateId!.Value, ct);

        var certificate = (await admin.GetOperatorDetails(operatorId, ct)).info.certificates.Values
           .Single(c => c.certificateId == enrolled.certificateId);
        var audit = await AuditAsync(admin, "RevokeOperatorCertificate", operatorId.ToString(), ct);

        Assert.Multiple(() =>
        {
            Assert.That(revoked.success, Is.True, revoked.error);
            Assert.That(certificate.isRevoked, Is.True);
            Assert.That(Vault.Revocations.Count(serial => serial == enrolled.serialNumber), Is.EqualTo(1),
                "revoking twice asked Vault twice, or never");
            Assert.That(again.success, Is.True, "revoking a revoked certificate is not an error");
            Assert.That((unknown.success, unknown.error), Is.EqualTo((false, "Certificate not found")));
            Assert.That((mine.success, mine.error), Is.EqualTo((false, "Cannot revoke your own certificate")));
            Assert.That(Vault.Revocations, Does.Not.Contain(own.serialNumber));
            Assert.That(before?.HasActiveCertificate, Is.True, "premise");
            Assert.That(after?.HasActiveCertificate, Is.False,
                "the identity server still believes the operator holds a live certificate");
            Assert.That(audit.Select(a => a.details), Has.Some.Contain(enrolled.serialNumber!));
        });
    }

    // ── Per-app access ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every grant, change and revocation is what the identity server sees on the operator's next
    /// sign-in, not two minutes later.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task App_access_is_granted_changed_and_revoked_and_the_identity_server_sees_each_at_once(
        CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var teamId = await SeedTeamAsync(owner.UserId, "Finance Tools", ct);
        var (appId, clientId) = await SeedAppAsync(teamId, "Ledger", true, ct);
        var aegis  = Aegis();

        var (scope, admin) = Admin();
        await using var _ = scope;

        var operatorId = await CreateOperatorAsync(admin, NewOperatorEmail("access"), null, ct);
        var target     = $"{operatorId}:{appId}";

        var anyBefore    = await aegis.HasAnyOperatorAppAccessAsync(operatorId, ct);
        var accessBefore = await aegis.GetOperatorAppAccessAsync(operatorId, appId, ct);

        var granted = await admin.GrantOperatorAppAccess(
            new GrantOperatorAppAccessInput(operatorId, appId, new(["openid", "ledger.read"]), new(["finance:read"])), ct);
        var list         = await admin.GetOperatorAppAccess(operatorId, ct);
        var anyGranted   = await aegis.HasAnyOperatorAppAccessAsync(operatorId, ct);
        var accessGrant  = await aegis.GetOperatorAppAccessAsync(operatorId, appId, ct);
        var duplicate    = await admin.GrantOperatorAppAccess(
            new GrantOperatorAppAccessInput(operatorId, appId, new([]), new([])), ct);

        var updated = await admin.UpdateOperatorAppAccess(
            new UpdateOperatorAppAccessInput(operatorId, appId, new(["openid"]), new(["finance:write"]), true), ct);
        var accessUpdated = await aegis.GetOperatorAppAccessAsync(operatorId, appId, ct);

        var suspended = await admin.UpdateOperatorAppAccess(
            new UpdateOperatorAppAccessInput(operatorId, appId, new(["openid"]), new(["finance:write"]), false), ct);
        var listSuspended   = await admin.GetOperatorAppAccess(operatorId, ct);
        var accessSuspended = await aegis.GetOperatorAppAccessAsync(operatorId, appId, ct);

        var revoked      = await admin.RevokeOperatorAppAccess(operatorId, appId, ct);
        var listRevoked  = await admin.GetOperatorAppAccess(operatorId, ct);
        var anyRevoked   = await aegis.HasAnyOperatorAppAccessAsync(operatorId, ct);

        var grantAudit  = await AuditAsync(admin, "GrantOperatorAppAccess", target, ct);
        var updateAudit = await AuditAsync(admin, "UpdateOperatorAppAccess", target, ct);
        var revokeAudit = await AuditAsync(admin, "RevokeOperatorAppAccess", target, ct);

        var entry = list.entries.Values.SingleOrDefault();

        Assert.Multiple(() =>
        {
            Assert.That(anyBefore, Is.False, "premise");
            Assert.That(accessBefore, Is.Null, "premise");

            Assert.That(granted.success, Is.True, granted.error);
            Assert.That(entry?.appName, Is.EqualTo("Ledger"));
            Assert.That(entry?.appClientId, Is.EqualTo(clientId));
            Assert.That(entry?.allowedScopes.Values, Is.EqualTo(new[] { "openid", "ledger.read" }));
            Assert.That(entry?.claims.Values, Is.EqualTo(new[] { "finance:read" }));
            Assert.That(entry?.grantedBy, Is.EqualTo(SystemOperatorId));
            Assert.That(entry?.isActive, Is.True);
            Assert.That(anyGranted, Is.True, "the identity server still thinks the operator has no grants at all");
            Assert.That(accessGrant?.Claims, Is.EqualTo(new[] { "finance:read" }), "the identity server has not seen the grant");
            Assert.That((duplicate.success, duplicate.error),
                Is.EqualTo((false, "Access record already exists. Use UpdateOperatorAppAccess to modify it.")));

            Assert.That(updated.success, Is.True, updated.error);
            Assert.That(accessUpdated?.Claims, Is.EqualTo(new[] { "finance:write" }), "the identity server kept the old claims");

            Assert.That(suspended.success, Is.True, suspended.error);
            Assert.That(listSuspended.entries.Values.Single().isActive, Is.False);
            Assert.That(accessSuspended, Is.Null, "a suspended grant still signs the operator in with its claims");

            Assert.That(revoked.success, Is.True, revoked.error);
            Assert.That(listRevoked.entries.Size, Is.EqualTo(0));
            Assert.That(anyRevoked, Is.False, "the identity server still counts a revoked grant");

            Assert.That(grantAudit.Single().details, Does.Contain("Ledger").And.Contain(clientId));
            Assert.That(updateAudit, Has.Count.EqualTo(2));
            Assert.That(revokeAudit, Has.Count.EqualTo(1));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task App_access_is_refused_for_an_unknown_operator_app_or_record(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var teamId = await SeedTeamAsync(owner.UserId, "Refusal Tools", ct);
        var (appId, _) = await SeedAppAsync(teamId, "Refusal Tool", true, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var operatorId = await CreateOperatorAsync(admin, NewOperatorEmail(), null, ct);

        var unknownOperator = await admin.GrantOperatorAppAccess(new GrantOperatorAppAccessInput(Guid.NewGuid(), appId, new([]), new([])), ct);
        var unknownApp      = await admin.GrantOperatorAppAccess(new GrantOperatorAppAccessInput(operatorId, Guid.NewGuid(), new([]), new([])), ct);
        var updateMissing   = await admin.UpdateOperatorAppAccess(
            new UpdateOperatorAppAccessInput(operatorId, appId, new([]), new([]), true), ct);
        var revokeMissing   = await admin.RevokeOperatorAppAccess(operatorId, appId, ct);

        Assert.Multiple(() =>
        {
            Assert.That((unknownOperator.success, unknownOperator.error), Is.EqualTo((false, "Operator not found")));
            Assert.That((unknownApp.success, unknownApp.error), Is.EqualTo((false, "Application not found")));
            Assert.That((updateMissing.success, updateMissing.error), Is.EqualTo((false, "Access record not found")));
            Assert.That((revokeMissing.success, revokeMissing.error), Is.EqualTo((false, "Access record not found")));
        });
    }

    // ── The audit log ───────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task GetAuditLog_filters_by_the_time_window_it_is_given(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var from       = DateTimeOffset.UtcNow.AddSeconds(-5);
        var operatorId = await CreateOperatorAsync(admin, NewOperatorEmail("window"), null, ct);
        var to         = DateTimeOffset.UtcNow.AddSeconds(5);
        var target     = operatorId.ToString();

        async Task<AuditLogPage> Window(DateTimeOffset? since, DateTimeOffset? until)
            => await admin.GetAuditLog(new AuditLogQuery(SystemOperatorId, "CreateOperator", target, since, until, 0, 20), ct);

        var inside    = await Window(from, to);
        var tooEarly  = await Window(null, from.AddMinutes(-1));
        var tooLate   = await Window(to.AddMinutes(1), null);
        var otherGuy  = await admin.GetAuditLog(new AuditLogQuery(RegularOperatorId, "CreateOperator", target, null, null, 0, 20), ct);

        Assert.Multiple(() =>
        {
            Assert.That(inside.totalCount, Is.EqualTo(1));
            Assert.That(inside.entries.Values.Single().operatorEmail, Is.EqualTo(SystemOperatorEmail));
            Assert.That(inside.entries.Values.Single().targetType, Is.EqualTo("Operator"));
            Assert.That(tooEarly.totalCount, Is.EqualTo(0), "the upper bound was not applied");
            Assert.That(tooLate.totalCount, Is.EqualTo(0), "the lower bound was not applied");
            Assert.That(otherGuy.totalCount, Is.EqualTo(0), "the operator filter was not applied");
        });
    }
}
