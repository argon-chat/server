namespace ArgonComplexTest.Tests;

using System.Net;
using System.Text.Json;
using Argon.Entities;
using Argon.Features.Auth;
using Argon.Features.Clustering;
using Argon.Grains.Interfaces;
using Argon.Services;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Passkeys, through the whole WebAuthn ceremony rather than around it.
/// </summary>
/// <remarks>
/// <para><c>SecurityTests</c> seeds passkey rows directly, because completing a registration needs an
/// authenticator. <see cref="SoftwareAuthenticator"/> is one: it answers the server's options with a
/// real "none" attestation and real ES256 assertions, so the server's own Fido2 verification decides
/// every outcome here — registration, sign-in on the security screen, sign-in at the identity server,
/// and each way those can be refused.</para>
///
/// <para>The identity server's own host runs too, for the one-time codes its sign-ins send: those are
/// limited per caller address, and the address only exists on a request that arrived over HTTP.</para>
/// </remarks>
[TestFixture]
public class PasskeyCeremonyTests : TestBase
{
    /// <summary>A YubiKey 5 NFC, so the authenticator's name has something to resolve to.</summary>
    private static readonly Guid YubiKey5Nfc = Guid.Parse("ee882879-721c-4913-9775-3dfcce97072a");

    private RoleHost aegis = null!;

    [OneTimeSetUp]
    public void StartTheIdentityServer()
        => aegis = new RoleHost(ArgonTestEnvironment.Instance.Host.Settings, ArgonRoleId.Aegis,
            siloPort: 0, ArgonClusterEndpoints.DefaultClusterId);

    [OneTimeTearDown]
    public async Task StopTheIdentityServer()
        => await aegis.DisposeAsync();

    private Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync(ct);

    private IAuthorizationGrain Authorization => GetGrainFactory().GetGrain<IAuthorizationGrain>(Guid.NewGuid());

    private static async Task<Passkey> RegisterAsync(TestUserSession account, SoftwareAuthenticator authenticator,
        string name, CancellationToken ct)
    {
        var begun = await account.Security.BeginAddPasskey(name, ct);

        Assert.That(begun, Is.InstanceOf<SuccessBeginPasskey>(), $"could not begin: {(begun as FailedBeginPasskey)?.error}");

        var completed = await account.Security.CompleteAddPasskey(
            authenticator.Register(((SuccessBeginPasskey)begun).optionsJson), ct);

        Assert.That(completed, Is.InstanceOf<SuccessCompletePasskey>(),
            $"a well-formed registration was refused: {(completed as FailedCompletePasskey)?.error}");

        return ((SuccessCompletePasskey)completed).passkey;
    }

    private static async Task<string> BeginValidationAsync(TestUserSession account, CancellationToken ct)
    {
        var begun = await account.Security.BeginValidatePasskey(ct);

        Assert.That(begun, Is.InstanceOf<SuccessBeginValidatePasskey>(),
            $"could not begin a validation: {(begun as FailedBeginValidatePasskey)?.error}");

        return ((SuccessBeginValidatePasskey)begun).optionsJson;
    }

    [Test, CancelAfter(120_000)]
    public async Task A_registered_passkey_is_listed_and_named_after_its_authenticator(CancellationToken ct = default)
    {
        var account                  = await CreateSessionAsync(ct);
        using var authenticator      = new SoftwareAuthenticator(YubiKey5Nfc);

        var passkey = await RegisterAsync(account, authenticator, "Desk key", ct);

        var listed  = await account.Security.GetPasskeys(ct);
        var details = await account.Security.GetSecurityDetails(ct);

        await using var db = await NewDbAsync(ct);
        var row = await db.Passkeys.AsNoTracking().SingleAsync(p => p.Id == passkey.id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(passkey.name, Is.EqualTo("Desk key"));
            Assert.That(passkey.aaGuid, Is.EqualTo(YubiKey5Nfc));
            Assert.That(passkey.authenticatorName, Is.EqualTo("YubiKey 5 NFC"));
            Assert.That(passkey.lastUsedAt, Is.Null);

            Assert.That(listed.Values.Select(p => p.id), Is.EqualTo(new[] { passkey.id }));
            Assert.That(details.passkeys.Values.Select(p => p.id), Is.EqualTo(new[] { passkey.id }));

            Assert.That(row.CredentialId, Is.EqualTo(authenticator.CredentialId));
            Assert.That(row.IsCompleted, Is.True);
            Assert.That(row.PublicKey, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task The_same_authenticator_cannot_be_registered_twice(CancellationToken ct = default)
    {
        var account             = await CreateSessionAsync(ct);
        using var authenticator = new SoftwareAuthenticator(YubiKey5Nfc);

        await RegisterAsync(account, authenticator, "First", ct);

        var begun = await account.Security.BeginAddPasskey("Second", ct);

        using var options = JsonDocument.Parse(((SuccessBeginPasskey)begun).optionsJson);
        var excluded = options.RootElement.GetProperty("excludeCredentials").EnumerateArray()
           .Select(c => c.GetProperty("id").GetString())
           .ToList();

        var again = await account.Security.CompleteAddPasskey(
            authenticator.Register(((SuccessBeginPasskey)begun).optionsJson), ct);

        Assert.Multiple(() =>
        {
            Assert.That(excluded, Is.EqualTo(new[] { SoftwareAuthenticator.Base64UrlEncode(authenticator.CredentialId) }),
                "the options did not tell the browser which authenticator is already registered");
            Assert.That(again, Is.InstanceOf<FailedCompletePasskey>());
            Assert.That((again as FailedCompletePasskey)?.error, Is.EqualTo(PasskeyError.VERIFICATION_FAILED));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_registration_answer_needs_a_live_challenge_and_a_body(CancellationToken ct = default)
    {
        var account             = await CreateSessionAsync(ct);
        using var authenticator = new SoftwareAuthenticator(YubiKey5Nfc);

        var begun   = (SuccessBeginPasskey)await account.Security.BeginAddPasskey("Key", ct);
        var answer  = authenticator.Register(begun.optionsJson);
        var first   = await account.Security.CompleteAddPasskey(answer, ct);
        var replay  = await account.Security.CompleteAddPasskey(answer, ct);
        var blank   = await account.Security.CompleteAddPasskey("  ", ct);
        var nothing = await account.Security.CompleteAddPasskey("null", ct);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.InstanceOf<SuccessCompletePasskey>());
            Assert.That((replay as FailedCompletePasskey)?.error, Is.EqualTo(PasskeyError.CHALLENGE_EXPIRED),
                "the registration challenge outlived the registration it was issued for");
            Assert.That((blank as FailedCompletePasskey)?.error, Is.EqualTo(PasskeyError.INVALID_CREDENTIAL));
            Assert.That((nothing as FailedCompletePasskey)?.error, Is.EqualTo(PasskeyError.INVALID_CREDENTIAL));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Adding_a_passkey_needs_a_name_and_room(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);

        var unnamed = await account.Security.BeginAddPasskey("   ", ct);

        await using (var db = await NewDbAsync(ct))
        {
            for (var i = 0; i < 10; i++)
            {
                db.Passkeys.Add(new UserPasskeyEntity
                {
                    Id           = Guid.CreateVersion7(),
                    UserId       = account.UserId,
                    Name         = $"Key {i}",
                    CredentialId = Guid.NewGuid().ToByteArray(),
                    PublicKey    = new byte[32],
                    IsCompleted  = true,
                    CreatedAt    = DateTimeOffset.UtcNow,
                    UpdatedAt    = DateTimeOffset.UtcNow
                });
            }

            await db.SaveChangesAsync(ct);
        }

        var full = await account.Security.BeginAddPasskey("Eleventh", ct);

        Assert.Multiple(() =>
        {
            Assert.That((unnamed as FailedBeginPasskey)?.error, Is.EqualTo(PasskeyError.INVALID_CREDENTIAL));
            Assert.That((full as FailedBeginPasskey)?.error, Is.EqualTo(PasskeyError.LIMIT_REACHED));
        });
    }

    /// <summary>
    /// A name the passkey list cannot hold is refused before the ceremony, not after it.
    /// </summary>
    /// <remarks>
    /// <c>UserPasskeyEntity.Name</c> declares 128 characters, but the column is <c>text</c>, so nothing
    /// below the grain enforced it: a name of any length was stored and then sent to every device in
    /// every security-details update. The name is only held in the cache between the two steps, so
    /// the place to refuse it is the first one, before the person goes through their authenticator.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_passkey_name_longer_than_the_list_holds_is_refused_up_front(CancellationToken ct = default)
    {
        var account             = await CreateSessionAsync(ct);
        using var authenticator = new SoftwareAuthenticator(YubiKey5Nfc);

        var tooLong = await account.Security.BeginAddPasskey(new string('k', 129), ct);
        var longest = await RegisterAsync(account, authenticator, new string('k', 128), ct);

        Assert.Multiple(() =>
        {
            Assert.That((tooLong as FailedBeginPasskey)?.error, Is.EqualTo(PasskeyError.INVALID_CREDENTIAL));
            Assert.That(longest.name, Has.Length.EqualTo(128));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_valid_assertion_signs_in_once_and_advances_the_counter(CancellationToken ct = default)
    {
        var account             = await CreateSessionAsync(ct);
        using var authenticator = new SoftwareAuthenticator(YubiKey5Nfc);
        var passkey             = await RegisterAsync(account, authenticator, "Laptop", ct);

        var options = await BeginValidationAsync(account, ct);

        using (var parsed = JsonDocument.Parse(options))
        {
            var allowed = parsed.RootElement.GetProperty("allowCredentials").EnumerateArray()
               .Select(c => c.GetProperty("id").GetString());

            Assert.That(allowed, Is.EqualTo(new[] { SoftwareAuthenticator.Base64UrlEncode(authenticator.CredentialId) }));
        }

        var assertion = authenticator.Assert(options);
        var validated = await account.Security.CompleteValidatePasskey(assertion, ct);
        var replayed  = await account.Security.CompleteValidatePasskey(assertion, ct);

        await using var db = await NewDbAsync(ct);
        var row = await db.Passkeys.AsNoTracking().SingleAsync(p => p.Id == passkey.id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(validated, Is.InstanceOf<SuccessCompletePasskey>(),
                $"a valid assertion was refused: {(validated as FailedCompletePasskey)?.error}");
            Assert.That((validated as SuccessCompletePasskey)?.passkey.lastUsedAt, Is.Not.Null);
            Assert.That(row.SignCount, Is.EqualTo(1), "the signature counter was not recorded, so a clone would go unnoticed");
            Assert.That(row.LastUsedAt, Is.Not.Null);
            Assert.That((replayed as FailedCompletePasskey)?.error, Is.EqualTo(PasskeyError.CHALLENGE_EXPIRED),
                "a captured assertion validated a second time");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_forged_or_foreign_assertion_is_refused(CancellationToken ct = default)
    {
        var account             = await CreateSessionAsync(ct);
        using var authenticator = new SoftwareAuthenticator(YubiKey5Nfc);
        using var forger        = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

        await RegisterAsync(account, authenticator, "Phone", ct);

        var forged = await account.Security.CompleteValidatePasskey(
            authenticator.Assert(await BeginValidationAsync(account, ct), signWith: forger), ct);

        var foreign = await account.Security.CompleteValidatePasskey(
            authenticator.Assert(await BeginValidationAsync(account, ct), credentialId: Guid.NewGuid().ToByteArray()), ct);

        var blank   = await account.Security.CompleteValidatePasskey("", ct);
        var nothing = await account.Security.CompleteValidatePasskey("null", ct);

        Assert.Multiple(() =>
        {
            Assert.That((forged as FailedCompletePasskey)?.error, Is.EqualTo(PasskeyError.VERIFICATION_FAILED),
                "an assertion signed by the wrong key was accepted");
            Assert.That((foreign as FailedCompletePasskey)?.error, Is.EqualTo(PasskeyError.NOT_FOUND));
            Assert.That((blank as FailedCompletePasskey)?.error, Is.EqualTo(PasskeyError.INVALID_CREDENTIAL));
            Assert.That((nothing as FailedCompletePasskey)?.error, Is.EqualTo(PasskeyError.INVALID_CREDENTIAL));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Validating_needs_a_passkey_and_a_challenge(CancellationToken ct = default)
    {
        var account             = await CreateSessionAsync(ct);
        using var authenticator = new SoftwareAuthenticator(YubiKey5Nfc);

        var none = await account.Security.BeginValidatePasskey(ct);

        await RegisterAsync(account, authenticator, "Key", ct);

        // An assertion for a challenge this account never asked for.
        var unasked = await account.Security.CompleteValidatePasskey(
            authenticator.Assert(JsonSerializer.Serialize(new { challenge = "AAAA" })), ct);

        Assert.Multiple(() =>
        {
            Assert.That((none as FailedBeginValidatePasskey)?.error, Is.EqualTo(PasskeyError.NOT_FOUND));
            Assert.That((unasked as FailedCompletePasskey)?.error, Is.EqualTo(PasskeyError.CHALLENGE_EXPIRED));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_removed_passkey_no_longer_validates(CancellationToken ct = default)
    {
        var account             = await CreateSessionAsync(ct);
        using var authenticator = new SoftwareAuthenticator(YubiKey5Nfc);
        using var spare         = new SoftwareAuthenticator(YubiKey5Nfc);

        var passkey = await RegisterAsync(account, authenticator, "Lost key", ct);
        await RegisterAsync(account, spare, "Spare key", ct);

        var options = await BeginValidationAsync(account, ct);
        var removed = await account.Security.RemovePasskey(passkey.id, ct);
        var again   = await account.Security.RemovePasskey(passkey.id, ct);
        var result  = await account.Security.CompleteValidatePasskey(authenticator.Assert(options), ct);
        var listed  = await account.Security.GetPasskeys(ct);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.InstanceOf<SuccessRemovePasskey>());
            Assert.That((again as FailedRemovePasskey)?.error, Is.EqualTo(PasskeyError.NOT_FOUND));
            Assert.That((result as FailedCompletePasskey)?.error, Is.EqualTo(PasskeyError.NOT_FOUND),
                "a passkey the user removed still proved who they are");
            Assert.That(listed.Values.Select(p => p.name), Is.EqualTo(new[] { "Spare key" }));
        });
    }

    // ── signing in at the identity server ──────────────────────────────────────────────────────

    private static (string Nonce, string Options) ReadLoginOptions(BeginPasskeyLoginResult begun)
    {
        Assert.That(begun.Error, Is.EqualTo(PasskeyLoginError.NONE));

        using var body = JsonDocument.Parse(begun.OptionsJson!);
        return (body.RootElement.GetProperty("nonce").GetString()!, body.RootElement.GetProperty("options").GetRawText());
    }

    private static string LoginPayload(string nonce, string assertion)
        => JsonSerializer.Serialize(new { nonce, assertionResponseJson = assertion });

    [Test, CancelAfter(120_000)]
    public async Task A_passkey_signs_its_owner_in_without_a_password(CancellationToken ct = default)
    {
        var account             = await CreateSessionAsync(ct);
        using var authenticator = new SoftwareAuthenticator(YubiKey5Nfc);
        await RegisterAsync(account, authenticator, "Key", ct);

        var (nonce, options) = ReadLoginOptions(await Authorization.BeginPasskeyLogin(account.Credentials.email, ct));
        var assertion        = authenticator.Assert(options);

        var signedIn = await Authorization.CompletePasskeyLogin(LoginPayload(nonce, assertion), ct);
        var replayed = await Authorization.CompletePasskeyLogin(LoginPayload(nonce, assertion), ct);

        Assert.Multiple(() =>
        {
            Assert.That(signedIn.Error, Is.EqualTo(PasskeyLoginError.NONE));
            Assert.That(signedIn.Success, Is.True);
            Assert.That(signedIn.UserId, Is.EqualTo(account.UserId));
            Assert.That(signedIn.Token, Is.Not.Null.And.Not.Empty);
            Assert.That(signedIn.RequiresOtp, Is.False);
            Assert.That(replayed.Error, Is.EqualTo(PasskeyLoginError.CHALLENGE_EXPIRED), "a sign-in challenge answered twice");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_passkey_sign_in_asks_for_a_code_when_the_account_wants_one(CancellationToken ct = default)
    {
        var account             = await CreateSessionAsync(ct);
        using var authenticator = new SoftwareAuthenticator(YubiKey5Nfc);
        await RegisterAsync(account, authenticator, "Key", ct);

        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == account.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.PreferredAuthMode, ArgonAuthMode.PasskeyWithOtp), ct);

        var (nonce, options) = ReadLoginOptions(await Authorization.BeginPasskeyLogin(account.Credentials.email, ct));
        var verified         = await Authorization.CompletePasskeyLogin(LoginPayload(nonce, authenticator.Assert(options)), ct);

        Assert.That(verified.RequiresOtp, Is.True, $"the second factor was skipped: {verified.Error}");

        var code = await GetEmailCodeAsync(account.Credentials.email, ct: ct);

        var wrong    = await Authorization.ConfirmPasskeyOtp(verified.PasskeyNonce!, code == "000000" ? "111111" : "000000", ct);
        var right    = await Authorization.ConfirmPasskeyOtp(verified.PasskeyNonce!, code!, ct);
        var spent    = await Authorization.ConfirmPasskeyOtp(verified.PasskeyNonce!, code!, ct);
        var nonsense = await Authorization.ConfirmPasskeyOtp("", "", ct);

        Assert.Multiple(() =>
        {
            Assert.That(verified.Success, Is.False, "a token was issued before the second factor");
            Assert.That(verified.Token, Is.Null);
            Assert.That(code, Is.Not.Null, "no code was sent for the second factor");
            Assert.That(wrong.Error, Is.EqualTo(PasskeyLoginError.BAD_OTP));
            Assert.That(right.Success, Is.True, $"the right code was refused: {right.Error}");
            Assert.That(right.UserId, Is.EqualTo(account.UserId));
            Assert.That(right.Token, Is.Not.Null.And.Not.Empty);
            Assert.That(spent.Error, Is.EqualTo(PasskeyLoginError.NONCE_EXPIRED), "the passkey half of a sign-in was spent twice");
            Assert.That(nonsense.Error, Is.EqualTo(PasskeyLoginError.BAD_OTP));
        });
    }

    // ── codes sent by the identity server are limited per caller address ─────────────────────────

    /// <summary>
    /// A request to the identity server from a caller at <paramref name="address"/>.
    /// </summary>
    /// <remarks>
    /// Sent through the test server directly rather than an <see cref="HttpClient"/>, because the
    /// in-memory transport leaves the peer address unset for every caller alike — and the address is
    /// the thing under test.
    /// </remarks>
    private async Task<JsonElement> PostFromAsync(IPAddress address, string path, object body, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body);

        var context = await aegis.Server.SendAsync(c =>
        {
            c.Request.Method             = HttpMethods.Post;
            c.Request.Scheme             = "https";
            c.Request.Host               = new HostString("aegis.test.local");
            c.Request.Path               = path;
            c.Request.ContentType        = "application/json";
            c.Request.Body               = new MemoryStream(json);
            c.Connection.RemoteIpAddress = address;
        }, ct);

        using var reader = new StreamReader(context.Response.Body);
        var text = await reader.ReadToEndAsync(ct);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK), $"{path}: {text}");

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Two neighbouring addresses in a documentation range.</summary>
    private static (IPAddress Crowded, IPAddress Quiet) TwoAddresses(string prefix)
    {
        var host = Random.Shared.Next(1, 250);
        return (IPAddress.Parse($"{prefix}.{host}"), IPAddress.Parse($"{prefix}.{host + 1}"));
    }

    private static string CodeBucket(IPAddress address) => $"rl:otp:ip:{address}:hour";

    private async Task<TestUserSession> AccountSigningInWithAsync(ArgonAuthMode mode, CancellationToken ct)
    {
        var account = await CreateSessionAsync(ct);

        await using var db = await NewDbAsync(ct);
        await db.Users.Where(u => u.Id == account.UserId)
           .ExecuteUpdateAsync(set => set.SetProperty(u => u.PreferredAuthMode, mode), ct);

        return account;
    }

    /// <summary>
    /// The code a passkey sign-in asks for is counted against the caller's own address, so one
    /// address using up its allowance does not stop anyone else's code.
    /// </summary>
    /// <remarks>
    /// Every passkey-plus-code sign-in on the platform used to be counted against one address, the
    /// literal <c>"passkey-login"</c>: thirty sign-ins an hour, by anyone, and the next person passed
    /// the passkey step, was told a code was on its way, and got none. The crowded address stands in
    /// for thirty sign-ins from one place by putting its counter where they would have left it.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_passkey_sign_in_code_is_limited_by_the_callers_own_address(CancellationToken ct = default)
    {
        var (crowded, quiet) = TwoAddresses("198.51.100");
        var cache            = FactoryAsp.Services.GetRequiredService<IArgonCacheDatabase>();

        var fromCrowded = await AccountSigningInWithAsync(ArgonAuthMode.PasskeyWithOtp, ct);
        var fromQuiet   = await AccountSigningInWithAsync(ArgonAuthMode.PasskeyWithOtp, ct);

        using var crowdedKey = new SoftwareAuthenticator(YubiKey5Nfc);
        using var quietKey   = new SoftwareAuthenticator(YubiKey5Nfc);

        await RegisterAsync(fromCrowded, crowdedKey, "Key", ct);
        await RegisterAsync(fromQuiet, quietKey, "Key", ct);

        async Task<JsonElement> SignInAsync(IPAddress address, TestUserSession account, SoftwareAuthenticator key)
        {
            var begun = await PostFromAsync(address, "/api/auth/passkey/begin", new { email = account.Credentials.email }, ct);

            using var options = JsonDocument.Parse(begun.GetProperty("optionsJson").GetString()!);

            return await PostFromAsync(address, "/api/auth/passkey/complete", new
            {
                nonce                 = options.RootElement.GetProperty("nonce").GetString(),
                assertionResponseJson = key.Assert(options.RootElement.GetProperty("options").GetRawText())
            }, ct);
        }

        await cache.StringSetAsync(CodeBucket(crowded), "30", TimeSpan.FromMinutes(5), ct);

        try
        {
            var crowdedSignIn = await SignInAsync(crowded, fromCrowded, crowdedKey);
            var quietSignIn   = await SignInAsync(quiet, fromQuiet, quietKey);

            var crowdedCode = await GetEmailCodeAsync(fromCrowded.Credentials.email, TimeSpan.FromSeconds(2), ct);
            var quietCode   = await GetEmailCodeAsync(fromQuiet.Credentials.email, ct: ct);
            var quietCount  = await cache.StringGetAsync(CodeBucket(quiet), ct);

            Assert.Multiple(() =>
            {
                Assert.That(crowdedSignIn.GetProperty("requiresOtp").GetBoolean(), Is.True, crowdedSignIn.ToString());
                Assert.That(quietSignIn.GetProperty("requiresOtp").GetBoolean(), Is.True, quietSignIn.ToString());
                Assert.That(crowdedCode, Is.Null,
                    "an address past its allowance was sent a code anyway, so the limit was not counted against it");
                Assert.That(quietCode, Is.Not.Null,
                    "a caller's code was withheld because another address had used up its allowance");
                Assert.That(quietCount, Is.EqualTo("1"), "the code was not counted against the address that asked for it");
            });
        }
        finally
        {
            await cache.KeyDeleteAsync(CodeBucket(crowded), ct);
            await cache.KeyDeleteAsync(CodeBucket(quiet), ct);
        }
    }

    /// <summary>
    /// The same for the one-time code of an email-code sign-in at the identity server.
    /// </summary>
    /// <remarks>
    /// That path handed the code sender no address at all, so every such sign-in was counted against
    /// the empty one — the same shared allowance under another name.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_email_code_sign_in_is_limited_by_the_callers_own_address(CancellationToken ct = default)
    {
        var (crowded, quiet) = TwoAddresses("203.0.113");
        var cache            = FactoryAsp.Services.GetRequiredService<IArgonCacheDatabase>();

        var fromCrowded = await AccountSigningInWithAsync(ArgonAuthMode.EmailOtp, ct);
        var fromQuiet   = await AccountSigningInWithAsync(ArgonAuthMode.EmailOtp, ct);

        await cache.StringSetAsync(CodeBucket(crowded), "30", TimeSpan.FromMinutes(5), ct);

        try
        {
            var crowdedSignIn = await PostFromAsync(crowded, "/api/auth/oauth/authorize",
                new { email = fromCrowded.Credentials.email, clientId = "none" }, ct);
            var quietSignIn = await PostFromAsync(quiet, "/api/auth/oauth/authorize",
                new { email = fromQuiet.Credentials.email, clientId = "none" }, ct);

            var crowdedCode = await GetEmailCodeAsync(fromCrowded.Credentials.email, TimeSpan.FromSeconds(2), ct);
            var quietCode   = await GetEmailCodeAsync(fromQuiet.Credentials.email, ct: ct);
            var quietCount  = await cache.StringGetAsync(CodeBucket(quiet), ct);

            Assert.Multiple(() =>
            {
                Assert.That(crowdedSignIn.GetProperty("requiresOtp").GetBoolean(), Is.True, crowdedSignIn.ToString());
                Assert.That(quietSignIn.GetProperty("requiresOtp").GetBoolean(), Is.True, quietSignIn.ToString());
                Assert.That(crowdedCode, Is.Null, "an address past its allowance was sent a code anyway");
                Assert.That(quietCode, Is.Not.Null, "a caller's code was withheld because another address had used up its allowance");
                Assert.That(quietCount, Is.EqualTo("1"), "the code was not counted against the address that asked for it");
            });
        }
        finally
        {
            await cache.KeyDeleteAsync(CodeBucket(crowded), ct);
            await cache.KeyDeleteAsync(CodeBucket(quiet), ct);
        }
    }

    [Test, CancelAfter(120_000)]
    public async Task A_passkey_sign_in_is_refused_before_it_starts_for_an_account_that_cannot_finish(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);

        var unknown      = await Authorization.BeginPasskeyLogin($"nobody-{Guid.NewGuid():N}@test.local", ct);
        var noPasskeys   = await Authorization.BeginPasskeyLogin(account.Credentials.email, ct);
        var discoverable = await Authorization.BeginPasskeyLogin(null, ct);
        var noNonce      = await Authorization.CompletePasskeyLogin(JsonSerializer.Serialize(new { nonce = "" }), ct);
        var stale        = await Authorization.CompletePasskeyLogin(LoginPayload(Guid.NewGuid().ToString("N"), "{}"), ct);

        Assert.Multiple(() =>
        {
            Assert.That(unknown.Error, Is.EqualTo(PasskeyLoginError.USER_NOT_FOUND));
            Assert.That(noPasskeys.Error, Is.EqualTo(PasskeyLoginError.NO_PASSKEYS));
            Assert.That(discoverable.Error, Is.EqualTo(PasskeyLoginError.NONE),
                "without an email the browser offers whatever passkey it holds, so there is nothing to refuse");
            Assert.That(noNonce.Error, Is.EqualTo(PasskeyLoginError.INVALID_ASSERTION));
            Assert.That(stale.Error, Is.EqualTo(PasskeyLoginError.CHALLENGE_EXPIRED));
        });
    }
}
