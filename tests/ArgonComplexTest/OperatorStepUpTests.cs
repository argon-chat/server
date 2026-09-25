namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Argon.Entities;
using Argon.Features.Aegis;
using Argon.Features.Clustering;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using AccountContracts;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

/// <summary>
/// The staff step-up: turning a certificate into an operator, and what the directory says about one.
/// </summary>
/// <remarks>
/// <para><c>OperatorAuthChallengeGrain</c> has two ways in — a signature over a challenge it issued,
/// and a certificate the identity server's mutual-TLS route already took from a handshake — and one
/// set of checks behind both: the chain leads to the operator CA, an operator holds the certificate,
/// they are active, and (on the TLS route) they are the person signed in. The CA is
/// <see cref="TestOperatorPki"/>'s, since the suite runs no Vault.</para>
///
/// <para>The TLS route is driven once end to end through a real <c>aegis</c> host, header and all,
/// because that is the only caller the grain has; the rest of the refusals are asked of the grain
/// directly.</para>
/// </remarks>
[TestFixture]
public class OperatorStepUpTests : TestBase
{
    private const string RedirectUri = "https://app.test.local/callback";

    private RoleHost aegis = null!;
    private Guid     appId;

    [OneTimeSetUp]
    public async Task StartTheIdentityServerAndRegisterAnApplication()
    {
        aegis = new RoleHost(ArgonTestEnvironment.Instance.Host.Settings, ArgonRoleId.Aegis,
            siloPort: 0, ArgonClusterEndpoints.DefaultClusterId);

        var owner = await CreateSessionAsync();

        (appId, _) = await RegisterApplicationAsync(owner.UserId);
    }

    /// <summary>A web application owned by a team of one, registered for sign-in at the identity server.</summary>
    private async Task<(Guid AppId, string ClientId)> RegisterApplicationAsync(Guid ownerId)
    {
        var teams = GetGrainFactory().GetGrain<IDevTeamsGrain>(Guid.Empty);
        var team  = await teams.CreateTeamAsync(ownerId, $"stepup-{Guid.NewGuid():N}"[..24]);
        var app   = await teams.CreateClientAppAsync(team.teamId, "Step-up Test App", ClientAppPlatform.WebBased);

        await teams.AddRedirectAsync(team.teamId, app.appId, RedirectUri);
        await teams.UpdateScopeAsync(team.teamId, app.appId,
            new ScopeKeyValue(isRequired: true, ArgonScopes.UserRead, isLocked: false));

        return (app.appId, app.clientId);
    }

    [OneTimeTearDown]
    public async Task StopTheIdentityServer()
        => await aegis.DisposeAsync();

    private TestOperatorPki Pki => FactoryAsp.Services.GetRequiredService<TestOperatorPki>();

    private IIdentityDirectoryGrain Directory => GetGrainFactory().GetGrain<IIdentityDirectoryGrain>(Guid.Empty);

    private IOperatorAuthChallengeGrain StepUp(X509Certificate2 certificate)
        => GetGrainFactory().GetGrain<IOperatorAuthChallengeGrain>(ThumbprintOf(certificate));

    private static string ThumbprintOf(X509Certificate2 certificate)
        => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync(ct);

    private sealed record Operator(Guid Id, string Email, string DisplayName, X509Certificate2 Certificate);

    /// <summary>
    /// An operator with one certificate issued by the test CA, and the key that certificate is for.
    /// </summary>
    private async Task<Operator> EnrolAsync(
        AsymmetricAlgorithm key, Guid? userId = null, bool active = true, bool revoked = false, bool deleted = false,
        CancellationToken ct = default)
    {
        var suffix      = Guid.NewGuid().ToString("N")[..12];
        var certificate = Pki.Issue($"operator:{suffix}@argon.test", key);
        var now         = DateTimeOffset.UtcNow;

        var op = new OperatorEntity
        {
            Id          = Guid.CreateVersion7(),
            DisplayName = $"Operator {suffix}",
            Email       = $"op-{suffix}@argon.test",
            UserId      = userId,
            IsActive    = active,
            IsDeleted   = deleted,
            CreatedAt   = now,
            UpdatedAt   = now
        };

        var row = new OperatorCertificateEntity
        {
            Id           = Guid.CreateVersion7(),
            OperatorId   = op.Id,
            SerialNumber = certificate.SerialNumber,
            Thumbprint   = ThumbprintOf(certificate),
            Subject      = certificate.Subject,
            NotBefore    = certificate.NotBefore.ToUniversalTime(),
            NotAfter     = certificate.NotAfter.ToUniversalTime(),
            RevokedAt    = revoked ? now : null,
            CreatedAt    = now,
            UpdatedAt    = now
        };

        await using var db = await NewDbAsync(ct);

        db.Operators.Add(op);
        db.OperatorCertificates.Add(row);
        await db.SaveChangesAsync(ct);

        return new Operator(op.Id, op.Email, op.DisplayName, certificate);
    }

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static string Why(Argon.Either<OperatorAuthSuccess, OperatorAuthError> result)
        => result.IsSuccess ? "it succeeded" : result.Error.ToString();

    // ── the challenge path ──────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_challenge_signed_with_an_enrolled_key_identifies_the_operator(CancellationToken ct = default)
    {
        using var key = NewKey();
        var op        = await EnrolAsync(key, ct: ct);
        var before    = DateTimeOffset.UtcNow.AddSeconds(-1);

        var challenge = await StepUp(op.Certificate).CreateChallenge();
        var signature = key.SignData(challenge.ChallengeBytes, HashAlgorithmName.SHA256);

        var result = await StepUp(op.Certificate).VerifyChallenge(challenge.ChallengeId, signature, op.Certificate.RawData);

        Assert.That(result.IsSuccess, Is.True, $"an enrolled operator's own signature was refused: {Why(result)}");

        await using var db = await NewDbAsync(ct);
        var stored = await db.Operators.AsNoTracking().SingleAsync(o => o.Id == op.Id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(challenge.ChallengeBytes, Has.Length.EqualTo(32));
            Assert.That(result.Value.OperatorId, Is.EqualTo(op.Id));
            Assert.That(result.Value.Email, Is.EqualTo(op.Email));
            Assert.That(result.Value.DisplayName, Is.EqualTo(op.DisplayName));
            Assert.That(result.Value.CertificateThumbprint, Is.EqualTo(ThumbprintOf(op.Certificate)));
            Assert.That(result.Value.IsSystemOperator, Is.False);
            Assert.That(stored.LastAuthAt, Is.GreaterThan(before), "a successful step-up is not recorded against the operator");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_challenge_answers_once(CancellationToken ct = default)
    {
        using var key = NewKey();
        var op        = await EnrolAsync(key, ct: ct);
        var grain     = StepUp(op.Certificate);

        var challenge = await grain.CreateChallenge();
        var signature = key.SignData(challenge.ChallengeBytes, HashAlgorithmName.SHA256);

        var first  = await grain.VerifyChallenge(challenge.ChallengeId, signature, op.Certificate.RawData);
        var replay = await grain.VerifyChallenge(challenge.ChallengeId, signature, op.Certificate.RawData);
        var never  = await grain.VerifyChallenge(Guid.NewGuid().ToString("N"), signature, op.Certificate.RawData);

        Assert.Multiple(() =>
        {
            Assert.That(first.IsSuccess, Is.True);
            Assert.That(replay.IsSuccess, Is.False, "a captured signature signed the operator in a second time");
            Assert.That(replay.Error, Is.EqualTo(OperatorAuthError.ChallengeNotFound));
            Assert.That(never.Error, Is.EqualTo(OperatorAuthError.ChallengeNotFound));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task An_rsa_operator_key_is_verified_as_well_as_an_ec_one(CancellationToken ct = default)
    {
        using var key = RSA.Create(2048);
        var op        = await EnrolAsync(key, ct: ct);
        var grain     = StepUp(op.Certificate);

        var challenge = await grain.CreateChallenge();
        var signature = key.SignData(challenge.ChallengeBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var result = await grain.VerifyChallenge(challenge.ChallengeId, signature, op.Certificate.RawData);

        Assert.That(result.IsSuccess, Is.True, $"an RSA PIV key was refused: {Why(result)}");
        Assert.That(result.Value.OperatorId, Is.EqualTo(op.Id));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_signature_that_does_not_cover_the_challenge_is_refused(CancellationToken ct = default)
    {
        using var key      = NewKey();
        using var stranger = NewKey();
        var op             = await EnrolAsync(key, ct: ct);
        var grain          = StepUp(op.Certificate);

        var otherBytes = await grain.CreateChallenge();
        var wrongKey   = await grain.CreateChallenge();

        var overOtherBytes = await grain.VerifyChallenge(otherBytes.ChallengeId,
            key.SignData(RandomNumberGenerator.GetBytes(32), HashAlgorithmName.SHA256), op.Certificate.RawData);

        var byAnotherKey = await grain.VerifyChallenge(wrongKey.ChallengeId,
            stranger.SignData(wrongKey.ChallengeBytes, HashAlgorithmName.SHA256), op.Certificate.RawData);

        Assert.Multiple(() =>
        {
            Assert.That(overOtherBytes.Error, Is.EqualTo(OperatorAuthError.InvalidSignature));
            Assert.That(byAnotherKey.Error, Is.EqualTo(OperatorAuthError.InvalidSignature),
                "holding an operator's certificate without its key was enough to sign in as them");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Bytes_that_are_not_a_certificate_are_refused_on_both_paths(CancellationToken ct = default)
    {
        var garbage = RandomNumberGenerator.GetBytes(64);
        var grain   = GetGrainFactory().GetGrain<IOperatorAuthChallengeGrain>(Convert.ToHexString(SHA256.HashData(garbage)));

        var challenge = await grain.CreateChallenge();

        var signed = await grain.VerifyChallenge(challenge.ChallengeId, RandomNumberGenerator.GetBytes(64), garbage);
        var tls    = await grain.VerifyMutualTlsCertificate(garbage, Guid.NewGuid());

        Assert.Multiple(() =>
        {
            Assert.That(signed.Error, Is.EqualTo(OperatorAuthError.InvalidSignature));
            Assert.That(tls.Error, Is.EqualTo(OperatorAuthError.InvalidSignature));
        });
    }

    /// <summary>
    /// A certificate whose key is neither EC nor RSA cannot have signed anything the grain can check.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_certificate_for_a_key_type_the_verifier_does_not_take_is_refused(CancellationToken ct = default)
    {
        using var key   = DSA.Create(2048);
        var certificate = Pki.Issue("operator:dsa@argon.test", key);
        var grain       = StepUp(certificate);

        var challenge = await grain.CreateChallenge();
        var result    = await grain.VerifyChallenge(challenge.ChallengeId,
            key.SignData(challenge.ChallengeBytes, HashAlgorithmName.SHA256), certificate.RawData);

        Assert.That(result.Error, Is.EqualTo(OperatorAuthError.InvalidSignature));
    }

    // ── what holds whichever way the certificate arrived ────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_certificate_from_another_authority_is_not_trusted(CancellationToken ct = default)
    {
        using var key = NewKey();

        var request    = new CertificateRequest("CN=operator:self-signed@argon.test", key, HashAlgorithmName.SHA256);
        using var self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        var grain     = StepUp(self);
        var challenge = await grain.CreateChallenge();

        var signed = await grain.VerifyChallenge(challenge.ChallengeId,
            key.SignData(challenge.ChallengeBytes, HashAlgorithmName.SHA256), self.RawData);
        var tls = await grain.VerifyMutualTlsCertificate(self.RawData, Guid.NewGuid());

        Assert.Multiple(() =>
        {
            Assert.That(signed.Error, Is.EqualTo(OperatorAuthError.CertificateNotTrusted),
                "a certificate anyone can mint for themselves was taken for an operator's");
            Assert.That(tls.Error, Is.EqualTo(OperatorAuthError.CertificateNotTrusted));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_trusted_certificate_that_no_active_operator_holds_is_refused(CancellationToken ct = default)
    {
        using var unheldKey    = NewKey();
        using var revokedKey   = NewKey();
        using var deletedKey   = NewKey();
        using var inactiveKey  = NewKey();

        var unheld   = Pki.Issue("operator:nobody@argon.test", unheldKey);
        var revoked  = await EnrolAsync(revokedKey, revoked: true, ct: ct);
        var deleted  = await EnrolAsync(deletedKey, deleted: true, ct: ct);
        var inactive = await EnrolAsync(inactiveKey, active: false, ct: ct);

        var results = new Dictionary<string, OperatorAuthError?>();

        foreach (var (name, certificate, key) in new[]
                 {
                     ("unheld", unheld, unheldKey), ("revoked", revoked.Certificate, revokedKey),
                     ("deleted", deleted.Certificate, deletedKey), ("inactive", inactive.Certificate, inactiveKey)
                 })
        {
            var grain     = StepUp(certificate);
            var challenge = await grain.CreateChallenge();
            var result    = await grain.VerifyChallenge(challenge.ChallengeId,
                key.SignData(challenge.ChallengeBytes, HashAlgorithmName.SHA256), certificate.RawData);

            results[name] = result.IsSuccess ? null : (OperatorAuthError?)result.Error;
        }

        Assert.Multiple(() =>
        {
            Assert.That(results["unheld"], Is.EqualTo(OperatorAuthError.OperatorNotFound));
            Assert.That(results["revoked"], Is.EqualTo(OperatorAuthError.OperatorNotFound),
                "a revoked certificate still signs its operator in");
            Assert.That(results["deleted"], Is.EqualTo(OperatorAuthError.OperatorNotFound),
                "a deleted operator's certificate still signs them in");
            Assert.That(results["inactive"], Is.EqualTo(OperatorAuthError.OperatorInactive));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Over_tls_the_certificate_has_to_belong_to_the_signed_in_account(CancellationToken ct = default)
    {
        var owner      = await CreateSessionAsync(ct);
        using var key  = NewKey();
        var op         = await EnrolAsync(key, userId: owner.UserId, ct: ct);

        var someoneElse = await StepUp(op.Certificate).VerifyMutualTlsCertificate(op.Certificate.RawData, Guid.NewGuid());
        var theirOwn    = await StepUp(op.Certificate).VerifyMutualTlsCertificate(op.Certificate.RawData, owner.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(someoneElse.IsSuccess, Is.False,
                "a session borrowed an operator's key presented on another person's behalf");
            Assert.That(someoneElse.Error, Is.EqualTo(OperatorAuthError.CertificateUserMismatch));
            Assert.That(theirOwn.IsSuccess, Is.True, $"the operator's own certificate was refused: {Why(theirOwn)}");
            Assert.That(theirOwn.Value.OperatorId, Is.EqualTo(op.Id));
        });
    }

    /// <summary>
    /// The whole route: a signed-in session, the certificate in the header the proxy writes, the
    /// grain behind it, and the verification the rest of the authorization flow reads afterwards.
    /// </summary>
    [Test, CancelAfter(300_000)]
    public async Task The_identity_server_takes_the_certificate_from_the_proxy_header(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var intruder = await CreateSessionAsync(ct);

        using var key = NewKey();
        var op        = await EnrolAsync(key, userId: owner.UserId, ct: ct);
        var header    = Uri.EscapeDataString(op.Certificate.ExportCertificatePem());

        using var ownerClient    = AegisClient.For(aegis);
        using var intruderClient = AegisClient.For(aegis);

        // An application that is not yet approved admits only its own team, so each signs in to theirs.
        await SignInAsync(ownerClient, owner, (await RegisterApplicationAsync(owner.UserId)).ClientId, ct);
        await SignInAsync(intruderClient, intruder, (await RegisterApplicationAsync(intruder.UserId)).ClientId, ct);

        using var accepted = await VerifyAsync(ownerClient, header, ct);
        using var refused  = await VerifyAsync(intruderClient, header, ct);

        var acceptedBody = JObject.Parse(await accepted.Content.ReadAsStringAsync(ct));
        var refusedBody  = JObject.Parse(await refused.Content.ReadAsStringAsync(ct));

        var verifications = aegis.Services.GetRequiredService<IOperatorVerificationStore>();

        var ownerState    = await verifications.ReadAsync(owner.UserId, ct);
        var intruderState = await verifications.IsVerifiedAsync(intruder.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK), acceptedBody.ToString());
            Assert.That((Guid?)acceptedBody["operatorId"], Is.EqualTo(op.Id));
            Assert.That(ownerState?.OperatorId, Is.EqualTo(op.Id), "the step-up was not remembered for the flow it unlocks");
            Assert.That(ownerState?.CertThumbprint, Is.EqualTo(ThumbprintOf(op.Certificate)));

            Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((string?)refusedBody["error"], Is.EqualTo("certificate_validation_failed"));
            Assert.That(intruderState, Is.False, "someone else's certificate verified the intruder as an operator");
        });
    }

    private static async Task SignInAsync(HttpClient client, TestUserSession who, string clientId, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/oauth/authorize", new
        {
            email    = who.Credentials.email,
            password = who.Credentials.password,
            clientId
        }, ct);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"could not open an identity-server session: {await response.Content.ReadAsStringAsync(ct)}");
    }

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, string header, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/operator/verify");
        request.Headers.Add(new OperatorMutualTlsOptions().CertificateHeader, header);
        return client.SendAsync(request, ct);
    }

    // ── the directory ───────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task The_directory_finds_an_account_by_email_whatever_its_case(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);

        var exact   = await Directory.GetUserIdByEmailAsync(account.Credentials.email, ct);
        var shouted = await Directory.GetUserIdByEmailAsync(account.Credentials.email.ToUpperInvariant(), ct);
        var nobody  = await Directory.GetUserIdByEmailAsync($"nobody-{Guid.NewGuid():N}@test.local", ct);

        Assert.Multiple(() =>
        {
            Assert.That(exact, Is.EqualTo(account.UserId));
            Assert.That(shouted, Is.EqualTo(account.UserId), "the lookup compared the raw column, not the normalized one");
            Assert.That(nobody, Is.Null);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task The_directory_describes_the_operator_behind_an_account(CancellationToken ct = default)
    {
        var withKey    = await CreateSessionAsync(ct);
        var keyRevoked = await CreateSessionAsync(ct);
        var removed    = await CreateSessionAsync(ct);
        var plain      = await CreateSessionAsync(ct);

        using var a = NewKey();
        using var b = NewKey();
        using var c = NewKey();

        var active  = await EnrolAsync(a, userId: withKey.UserId, ct: ct);
        var lapsed  = await EnrolAsync(b, userId: keyRevoked.UserId, revoked: true, active: false, ct: ct);
        await EnrolAsync(c, userId: removed.UserId, deleted: true, ct: ct);

        var activeInfo  = await Directory.GetUserOperatorInfoAsync(withKey.UserId, ct);
        var lapsedInfo  = await Directory.GetUserOperatorInfoAsync(keyRevoked.UserId, ct);
        var removedInfo = await Directory.GetUserOperatorInfoAsync(removed.UserId, ct);
        var plainInfo   = await Directory.GetUserOperatorInfoAsync(plain.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(activeInfo, Is.EqualTo(new OperatorBasicInfo(active.Id, active.Email, active.DisplayName,
                HasActiveCertificate: true, IsActive: true, IsSystemOperator: false)));
            Assert.That(lapsedInfo?.OperatorId, Is.EqualTo(lapsed.Id));
            Assert.That(lapsedInfo?.HasActiveCertificate, Is.False, "a revoked certificate still counts as a usable one");
            Assert.That(lapsedInfo?.IsActive, Is.False);
            Assert.That(removedInfo, Is.Null, "a deleted operator is still described as one");
            Assert.That(plainInfo, Is.Null);
        });
    }

    /// <summary>
    /// A grant is found only while active, and any grant at all — even a withdrawn one — puts the
    /// operator on the explicit-allow model.
    /// </summary>
    /// <remarks>
    /// The second half is the one that matters. An operator with no grants reaches every internal
    /// application; if withdrawing an operator's last grant made them "have none" again, revoking
    /// access would widen it to everything.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Operator_app_access_is_found_only_while_active(CancellationToken ct = default)
    {
        using var a = NewKey();
        using var b = NewKey();
        using var c = NewKey();

        var granted   = await EnrolAsync(a, ct: ct);
        var withdrawn = await EnrolAsync(b, ct: ct);
        var never     = await EnrolAsync(c, ct: ct);

        await using (var db = await NewDbAsync(ct))
        {
            db.OperatorAppAccess.Add(new OperatorAppAccessEntity
            {
                OperatorId = granted.Id, AppId = appId, AllowedScopes = [ArgonScopes.UserRead],
                Claims = ["finance:read"], GrantedBy = granted.Id, IsActive = true
            });
            db.OperatorAppAccess.Add(new OperatorAppAccessEntity
            {
                OperatorId = withdrawn.Id, AppId = appId, GrantedBy = granted.Id, IsActive = false
            });
            await db.SaveChangesAsync(ct);
        }

        var grant        = await Directory.GetOperatorAppAccessAsync(granted.Id, appId, ct);
        var otherApp     = await Directory.GetOperatorAppAccessAsync(granted.Id, Guid.NewGuid(), ct);
        var inactive     = await Directory.GetOperatorAppAccessAsync(withdrawn.Id, appId, ct);
        var grantedAny   = await Directory.GetOperatorHasAnyAppAccessAsync(granted.Id, ct);
        var withdrawnAny = await Directory.GetOperatorHasAnyAppAccessAsync(withdrawn.Id, ct);
        var neverAny     = await Directory.GetOperatorHasAnyAppAccessAsync(never.Id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(grant?.OperatorId, Is.EqualTo(granted.Id));
            Assert.That(grant?.AppId, Is.EqualTo(appId));
            Assert.That(grant?.AllowedScopes, Is.EqualTo(new[] { ArgonScopes.UserRead }));
            Assert.That(grant?.Claims, Is.EqualTo(new[] { "finance:read" }));
            Assert.That(grant?.IsActive, Is.True);
            Assert.That(otherApp, Is.Null);
            Assert.That(inactive, Is.Null, "a withdrawn grant still opens the application");

            Assert.That(grantedAny, Is.True);
            Assert.That(withdrawnAny, Is.True,
                "withdrawing an operator's only grant put them back on the permissive model, which reaches every app");
            Assert.That(neverAny, Is.False);
        });
    }
}
