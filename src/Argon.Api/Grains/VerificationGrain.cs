namespace Argon.Grains;

using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Features.CoreLogic.Otp;
using Argon.Core.Features.CoreLogic.Passkeys;
using Argon.Core.Features.CoreLogic.Verification;
using Fido2NetLib;
using ion.runtime;
using OtpNet;
using Services;

/// <summary>
/// Step-up verification flows, one at a time per account.
/// </summary>
/// <remarks>
/// <para>Not a stateless worker on purpose: one activation per account serialises every guess, so the
/// attempt budget cannot be raced past by parallel requests. The flow itself lives in the cache under a
/// TTL rather than in the activation, so a deactivation or a deploy does not lose it and an abandoned one
/// needs no sweeper.</para>
///
/// <para>One budget of wrong answers covers the whole flow — password, codes, the new contact's code —
/// and spending it throws the flow away. Begins and sent codes are capped per hour, which is what bounds
/// password guessing through here and mail sent to the account's address.</para>
/// </remarks>
public sealed class VerificationGrain(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPasswordHashingService passwords,
    ITotpKeyStore totpKeyStore,
    IFido2 fido2,
    IArgonCacheDatabase cache,
    ILogger<VerificationGrain> logger) : Grain, IVerificationGrain
{
    private static readonly TimeSpan FlowLifetime   = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CodeLifetime   = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    private const           int      MaxAttempts    = 5;
    private const           int      BeginsPerHour  = 10;
    private const           int      CodesPerHour   = 10;

    private Guid UserId => this.GetPrimaryKey();

    private string FlowKey => $"verification:flow:{UserId}";

    public async Task<IBeginVerificationResult> BeginAsync(SensitiveAction action, Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            if (!await WithinHourlyCapAsync($"rl:verification:begin:{UserId}", BeginsPerHour, ct))
                return new FailedBeginVerification(VerificationError.RATE_LIMITED);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId, ct);
            if (user is null)
                return new FailedBeginVerification(VerificationError.INTERNAL_ERROR);

            var hasPasskey = await db.Passkeys.AnyAsync(
                p => p.UserId == UserId && p.IsCompleted && !p.IsDeleted && p.CredentialId != null, ct);

            var enrolled     = new EnrolledFactors(!string.IsNullOrEmpty(user.PasswordDigest), !string.IsNullOrEmpty(user.TotpSecret), hasPasskey);
            var requirements = VerificationPolicy.For(action, enrolled);
            if (requirements is null)
                return new FailedBeginVerification(VerificationError.FACTOR_NOT_ALLOWED);

            var flow = new FlowState
            {
                FlowId       = Guid.NewGuid(),
                Action       = action,
                SessionId    = sessionId,
                ExpiresAt    = DateTimeOffset.UtcNow + FlowLifetime,
                Requirements = requirements.Select(f => new RequirementState { Factors = f }).ToList(),
                AttemptsLeft = MaxAttempts
            };

            await SaveAsync(flow, ct);

            return new SuccessBeginVerification(ToContract(flow));
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to begin {Action} verification for user {UserId}", action, UserId);
            return new FailedBeginVerification(VerificationError.INTERNAL_ERROR);
        }
    }

    public async Task<IChallengeVerificationResult> ChallengeAsync(Guid flowId, VerificationFactor factor, Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            var flow = await LoadAsync(flowId, sessionId, ct);
            if (flow is null)
                return new FailedChallengeVerification(VerificationError.FLOW_EXPIRED, null);

            if (Pending(flow, factor) is null)
                return new FailedChallengeVerification(VerificationError.FACTOR_NOT_ALLOWED, null);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            switch (factor)
            {
                case VerificationFactor.EMAIL_CODE:
                {
                    if (flow.Code is { } previous && previous.ResendAt > DateTimeOffset.UtcNow)
                        return new FailedChallengeVerification(VerificationError.RATE_LIMITED, previous.ResendAt.UtcDateTime);

                    if (!await WithinHourlyCapAsync(CodesKey, CodesPerHour, ct))
                        return new FailedChallengeVerification(VerificationError.RATE_LIMITED, null);

                    var email = await db.Users.Where(u => u.Id == UserId).Select(u => u.Email).FirstAsync(ct);
                    var code  = OtpSecurity.GenerateNumericCode(6);

                    flow.Code = CodeState.For(code);
                    await SaveAsync(flow, ct);

                    await GrainFactory.GetGrain<IEmailManager>(Guid.NewGuid())
                       .SendVerificationCodeAsync(email, code, flow.Action, CodeLifetime);

                    return new VerificationCodeSent(ContactMask.Email(email), flow.Code.ResendAt.UtcDateTime);
                }
                case VerificationFactor.PASSKEY:
                {
                    var options = await PasskeyAssertion.BeginAsync(db, fido2, UserId, ct);
                    if (options is null)
                        return new FailedChallengeVerification(VerificationError.FACTOR_NOT_ALLOWED, null);

                    flow.PasskeyOptions = options;
                    await SaveAsync(flow, ct);

                    return new VerificationPasskeyOptions(options);
                }
                default:
                    return new FailedChallengeVerification(VerificationError.FACTOR_NOT_ALLOWED, null);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to challenge {Factor} for user {UserId}", factor, UserId);
            return new FailedChallengeVerification(VerificationError.INTERNAL_ERROR, null);
        }
    }

    public async Task<ISubmitVerificationResult> SubmitAsync(Guid flowId, VerificationFactor factor, string proof, Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            var flow = await LoadAsync(flowId, sessionId, ct);
            if (flow is null)
                return new FailedSubmitVerification(VerificationError.FLOW_EXPIRED, 0);

            var requirement = Pending(flow, factor);
            if (requirement is null)
                return new FailedSubmitVerification(VerificationError.FACTOR_NOT_ALLOWED, flow.AttemptsLeft);

            bool? accepted = factor switch
            {
                VerificationFactor.PASSWORD   => await CheckPasswordAsync(proof, ct),
                VerificationFactor.EMAIL_CODE => flow.Code is { } code && code.ExpiresAt > DateTimeOffset.UtcNow ? code.Matches(proof) : null,
                VerificationFactor.TOTP       => await CheckTotpAsync(proof, ct),
                VerificationFactor.PASSKEY    => flow.PasskeyOptions is { } options ? await CheckPasskeyAsync(options, proof, ct) : null,
                _                             => false
            };

            if (accepted is null)
                return new FailedSubmitVerification(VerificationError.CHALLENGE_REQUIRED, flow.AttemptsLeft);

            if (accepted is false)
            {
                var left = await SpendAttemptAsync(flow, ct);
                return new FailedSubmitVerification(left == 0 ? VerificationError.TOO_MANY_ATTEMPTS : VerificationError.INVALID_PROOF, left);
            }

            requirement.Satisfied = true;

            if (factor == VerificationFactor.EMAIL_CODE)
                flow.Code = null;
            if (factor == VerificationFactor.PASSKEY)
                flow.PasskeyOptions = null;
            if (flow.Verified)
                flow.ExpiresAt = DateTimeOffset.UtcNow + FlowLifetime;

            await SaveAsync(flow, ct);

            return new SuccessSubmitVerification(ToContract(flow));
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to submit {Factor} for user {UserId}", factor, UserId);
            return new FailedSubmitVerification(VerificationError.INTERNAL_ERROR, 0);
        }
    }

    public async Task CancelAsync(Guid flowId, Guid sessionId, CancellationToken ct = default)
    {
        if (await LoadAsync(flowId, sessionId, ct) is not null)
            await cache.KeyDeleteAsync(FlowKey, ct);
    }

    public async Task<bool> IsVerifiedAsync(Guid flowId, SensitiveAction action, Guid sessionId, CancellationToken ct = default)
        => await LoadAsync(flowId, sessionId, ct) is { Verified: true } flow && flow.Action == action;

    public async Task<TargetCodeSend> SendTargetCodeAsync(Guid flowId, SensitiveAction action, Guid sessionId, string target, CancellationToken ct = default)
    {
        var flow = await LoadAsync(flowId, sessionId, ct);
        if (flow is not { Verified: true } || flow.Action != action)
            return new TargetCodeSend { Outcome = TargetCodeOutcome.NotVerified };

        var sameTarget = string.Equals(flow.Target, target, StringComparison.OrdinalIgnoreCase);

        if (sameTarget && flow.TargetCode is { } previous && previous.ResendAt > DateTimeOffset.UtcNow)
            return new TargetCodeSend { Outcome = TargetCodeOutcome.TooSoon, ResendAt = previous.ResendAt };

        if (!await WithinHourlyCapAsync(CodesKey, CodesPerHour, ct))
            return new TargetCodeSend { Outcome = TargetCodeOutcome.RateLimited };

        var code = OtpSecurity.GenerateNumericCode(6);

        flow.Target     = target;
        flow.TargetCode = CodeState.For(code);
        await SaveAsync(flow, ct);

        switch (action)
        {
            case SensitiveAction.CHANGE_EMAIL:
                await GrainFactory.GetGrain<IEmailManager>(Guid.NewGuid()).SendOtpCodeAsync(target, code, CodeLifetime);
                break;
        }

        return new TargetCodeSend { Outcome = TargetCodeOutcome.Sent, ResendAt = flow.TargetCode.ResendAt };
    }

    public async Task<TargetCodeCheck> CheckTargetCodeAsync(Guid flowId, SensitiveAction action, Guid sessionId, string code, CancellationToken ct = default)
    {
        var flow = await LoadAsync(flowId, sessionId, ct);
        if (flow is not { Verified: true } || flow.Action != action)
            return new TargetCodeCheck { Outcome = TargetCheckOutcome.NotVerified };

        if (flow.TargetCode is not { } expected || expected.ExpiresAt <= DateTimeOffset.UtcNow)
            return new TargetCodeCheck { Outcome = TargetCheckOutcome.NoCode };

        if (!expected.Matches(code))
            return new TargetCodeCheck
            {
                Outcome = await SpendAttemptAsync(flow, ct) == 0 ? TargetCheckOutcome.Exhausted : TargetCheckOutcome.Invalid
            };

        var target = flow.Target;

        flow.TargetCode = null;
        await SaveAsync(flow, ct);

        return new TargetCodeCheck { Outcome = TargetCheckOutcome.Verified, Target = target };
    }

    public async Task CompleteAsync(Guid flowId, CancellationToken ct = default)
    {
        if (await ReadAsync(ct) is { } flow && flow.FlowId == flowId)
            await cache.KeyDeleteAsync(FlowKey, ct);
    }

    private string CodesKey => $"rl:verification:codes:{UserId}";

    // The first unsatisfied requirement this factor answers.
    private static RequirementState? Pending(FlowState flow, VerificationFactor factor)
        => flow.Requirements.FirstOrDefault(r => !r.Satisfied && r.Factors.Contains(factor));

    private async Task<bool> CheckPasswordAsync(string proof, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == UserId, ct);

        return passwords.VerifyPassword(proof, user);
    }

    private async Task<bool> CheckTotpAsync(string proof, CancellationToken ct)
    {
        var secret = await totpKeyStore.GetSecret(UserId, ct);

        return secret is not null && new Totp(secret).VerifyTotp(proof, out _, VerificationWindow.RfcSpecifiedNetworkDelay);
    }

    private async Task<bool> CheckPasskeyAsync(string optionsJson, string proof, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        try
        {
            return await PasskeyAssertion.CompleteAsync(db, fido2, UserId, optionsJson, proof, ct) is not null;
        }
        catch (Exception e) when (e is Fido2VerificationException or JsonException or FormatException)
        {
            logger.LogWarning(e, "Passkey step-up did not verify for user {UserId}", UserId);
            return false;
        }
    }

    private async Task<int> SpendAttemptAsync(FlowState flow, CancellationToken ct)
    {
        flow.AttemptsLeft = Math.Max(0, flow.AttemptsLeft - 1);

        if (flow.AttemptsLeft == 0)
        {
            logger.LogWarning("Verification flow for {Action} of user {UserId} ran out of attempts", flow.Action, UserId);
            await cache.KeyDeleteAsync(FlowKey, ct);
        }
        else
            await SaveAsync(flow, ct);

        return flow.AttemptsLeft;
    }

    private async Task<bool> WithinHourlyCapAsync(string key, int max, CancellationToken ct)
    {
        var count = await cache.StringIncrementAsync(key, ct);
        if (count == 1)
            await cache.UpdateStringExpirationAsync(key, TimeSpan.FromHours(1), ct);

        return count <= max;
    }

    private async Task<FlowState?> LoadAsync(Guid flowId, Guid sessionId, CancellationToken ct)
        => await ReadAsync(ct) is { } flow && flow.FlowId == flowId && flow.SessionId == sessionId ? flow : null;

    private async Task<FlowState?> ReadAsync(CancellationToken ct)
    {
        var json = await cache.StringGetAsync(FlowKey, ct);
        if (json is null)
            return null;

        var flow = JsonSerializer.Deserialize<FlowState>(json);

        return flow is not null && flow.ExpiresAt > DateTimeOffset.UtcNow ? flow : null;
    }

    private Task SaveAsync(FlowState flow, CancellationToken ct)
        => cache.StringSetAsync(FlowKey, JsonSerializer.Serialize(flow), flow.ExpiresAt - DateTimeOffset.UtcNow, ct);

    private static VerificationFlow ToContract(FlowState flow) => new(
        flow.FlowId,
        flow.Action,
        new IonArray<VerificationRequirement>(flow.Requirements.Select(r => new VerificationRequirement(new IonArray<VerificationFactor>(r.Factors), r.Satisfied)).ToList()),
        flow.Verified,
        flow.ExpiresAt.UtcDateTime,
        flow.AttemptsLeft);

    private sealed class FlowState
    {
        public required Guid                   FlowId         { get; init; }
        public required SensitiveAction        Action         { get; init; }
        public required Guid                   SessionId      { get; init; }
        public required DateTimeOffset         ExpiresAt      { get; set; }
        public required List<RequirementState> Requirements   { get; init; }
        public required int                    AttemptsLeft   { get; set; }
        public          CodeState?             Code           { get; set; }
        public          string?                PasskeyOptions { get; set; }
        public          string?                Target         { get; set; }
        public          CodeState?             TargetCode     { get; set; }

        [JsonIgnore]
        public bool Verified => Requirements.All(r => r.Satisfied);
    }

    private sealed class RequirementState
    {
        public required VerificationFactor[] Factors   { get; init; }
        public          bool                 Satisfied { get; set; }
    }

    private sealed record CodeState(string Hash, string Salt, DateTimeOffset ExpiresAt, DateTimeOffset ResendAt)
    {
        public static CodeState For(string code)
        {
            var salt = OtpSecurity.GenerateSalt(16);
            var now  = DateTimeOffset.UtcNow;

            return new CodeState(
                Convert.ToBase64String(OtpSecurity.ComputeHmac(salt, code)),
                Convert.ToBase64String(salt),
                now + CodeLifetime,
                now + ResendCooldown);
        }

        public bool Matches(string code)
            => OtpSecurity.ConstantTimeEquals(
                OtpSecurity.ComputeHmac(Convert.FromBase64String(Salt), code.Trim()),
                Convert.FromBase64String(Hash));
    }
}
