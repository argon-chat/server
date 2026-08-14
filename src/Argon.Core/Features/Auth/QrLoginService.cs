namespace Argon.Features.Auth;

using System.Security.Cryptography;
using Argon.Services;
using Argon.Services.Ion;

/// <summary>
/// The cross-device sign-in the instance manifest advertises as <c>qrLoginEnabled</c>: an already
/// signed-in phone vouches for a browser that has no credentials of its own.
/// </summary>
/// <remarks>
/// Modelled on <see cref="Argon.Api.Features.CoreLogic.Otp.IOtpService"/> rather than on a grain,
/// because a login request has no identity to be keyed by until it is approved — the desktop is
/// anonymous, and a grain per random token would activate once and never be addressed again. What
/// it does share with the OTP path is the store: one short-lived record in the shared cache, burnt
/// on the single read that consumes it.
/// </remarks>
public interface IQrLoginService
{
    Task<ICreateLoginRequestResult>  CreateAsync(CancellationToken ct = default);
    Task<ILoginPollResult>           PollAsync(string token, CancellationToken ct = default);
    Task<ILoginRequestPreviewResult> PreviewAsync(string token, Guid userId, CancellationToken ct = default);
    Task<IApproveLoginRequestResult> ApproveAsync(string token, Guid userId, CancellationToken ct = default);
    Task<IRejectLoginRequestResult>  RejectAsync(string token, Guid userId, CancellationToken ct = default);
}

/// <summary>The stage a pending sign-in has reached. Only <see cref="Pending"/> is scannable.</summary>
public enum QrLoginStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>
/// Everything the phone has to show and everything the poll has to re-check, in one record.
/// </summary>
/// <remarks>
/// <para>The identifying fields are exactly the ones <c>ArgonIonTicket</c> already collects off the
/// request — client name, ip, region, machine — so the approval card describes the browser using
/// the same facts the rest of the platform attributes a request by, and nothing new is gathered for
/// this feature alone.</para>
///
/// <para><see cref="Token"/> and <see cref="RefreshToken"/> are filled only by an approval, and are
/// gone from the cache the moment the desktop reads them. Holding a minted JWT at rest for the few
/// seconds between the phone's tap and the desktop's next poll is what <c>TransportExchange</c>
/// already does for its own handover; the alternative is a second round trip on a code that would
/// then need its own lifetime and its own burn.</para>
/// </remarks>
public sealed record QrLoginRecord
{
    public required string        ClientName   { get; init; }
    public required string?       HostName     { get; init; }
    public required string        Ip           { get; init; }
    public required string        Region       { get; init; }
    public required string        MachineId    { get; init; }
    public required DateTime      CreatedAt    { get; init; }
    public required DateTime      ExpiresAt    { get; init; }
    public          QrLoginStatus Status       { get; set; }
    public          Guid?         UserId       { get; set; }
    public          string?       Token        { get; set; }
    public          string?       RefreshToken { get; set; }
}

public sealed class QrLoginService(
    IArgonCacheDatabase cache,
    UserManagerService userManager,
    ILogger<QrLoginService> logger) : IQrLoginService
{
    /// <summary>
    /// How long an unapproved request stays scannable.
    /// </summary>
    /// <remarks>
    /// Two minutes is roughly "unlock the phone, open the app, aim". Longer would leave a working
    /// credential on a screen in a co-working space after its owner has walked away, and the desktop
    /// simply asks for another one — <see cref="CreateAsync"/> is cheap and the QR redraws.
    /// </remarks>
    private static readonly TimeSpan RequestTtl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long an approved request stays claimable.
    /// </summary>
    /// <remarks>
    /// Deliberately shorter than <see cref="RequestTtl"/> and re-armed at the moment of approval:
    /// once a real JWT is sitting in the record, the window in which anything can go wrong should be
    /// the desktop's next poll interval, not the remainder of the original two minutes.
    /// </remarks>
    private static readonly TimeSpan ApprovedTtl = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long a rejection is remembered.
    /// </summary>
    /// <remarks>
    /// Long enough for the desktop to poll once and say so out loud. Deleting the record outright
    /// would report the refusal as "not found", which reads like an expiry — and "your code expired"
    /// invites a retry, which is the opposite of what a user who just pressed «Это не я» wants.
    /// </remarks>
    private static readonly TimeSpan RejectedTtl = TimeSpan.FromSeconds(30);

    private static string Key(string token) => $"qr:login:{token}";

    public async Task<ICreateLoginRequestResult> CreateAsync(CancellationToken ct = default)
    {
        var ctx = ArgonRequestContext.Current;

        if (ctx.MachineId is null)
            return new FailedCreateLoginRequest(LoginRequestError.INTERNAL_ERROR);

        if (!await AllowAsync($"rl:qr:create:ip:{ctx.Ip}", max: 20, TimeSpan.FromMinutes(5), ct))
            return new FailedCreateLoginRequest(LoginRequestError.RATE_LIMITED);

        var now   = DateTime.UtcNow;
        var token = MintToken();

        var record = new QrLoginRecord
        {
            ClientName = ctx.ClientName,

            // Nothing on the wire carries the desktop's own hostname — neither the ion request
            // context nor any header the clients send — so this stays null rather than being
            // reconstructed from something that only looks like one. See the remarks on
            // LoginRequestPreview.hostName in IdentityInteraction.ion.
            HostName  = null,
            Ip        = ctx.Ip,
            Region    = ctx.Region,
            MachineId = ctx.MachineId,
            CreatedAt = now,
            ExpiresAt = now + RequestTtl,
            Status    = QrLoginStatus.Pending,
        };

        await cache.StringSetAsync(Key(token), JsonConvert.SerializeObject(record), RequestTtl, ct);

        return new SuccessCreateLoginRequest(new LoginRequestTicket(token, record.ExpiresAt));
    }

    public async Task<ILoginPollResult> PollAsync(string token, CancellationToken ct = default)
    {
        var ctx = ArgonRequestContext.Current;

        if (!await AllowAsync($"rl:qr:poll:ip:{ctx.Ip}", max: 600, TimeSpan.FromMinutes(5), ct))
            return new FailedLoginPoll(LoginRequestError.RATE_LIMITED);

        var record = await ReadAsync(token, ct);

        if (record is null)
            return new FailedLoginPoll(LoginRequestError.NOT_FOUND);

        // The poll is anonymous, so the only thing that distinguishes the browser that asked for the
        // code from anyone who photographed it off the screen is the machine cookie it was minted
        // against. Without this check the approved JWT goes to whoever polls first.
        if (!string.Equals(record.MachineId, ctx.MachineId, StringComparison.Ordinal))
            return new FailedLoginPoll(LoginRequestError.DEVICE_MISMATCH);

        switch (record.Status)
        {
            case QrLoginStatus.Rejected:
                await cache.KeyDeleteAsync(Key(token), ct);
                return new RejectedLoginRequest();

            case QrLoginStatus.Approved:
                // Burnt before the tokens leave the method: a poll that races another poll, or one
                // whose response never arrives, must not be answerable twice.
                await cache.KeyDeleteAsync(Key(token), ct);
                return new ApprovedLoginRequest(record.Token!, record.RefreshToken);

            default:
                return new PendingLoginRequest(record.ExpiresAt);
        }
    }

    public async Task<ILoginRequestPreviewResult> PreviewAsync(string token, Guid userId, CancellationToken ct = default)
    {
        if (!await AllowAsync($"rl:qr:preview:user:{userId}", max: 60, TimeSpan.FromMinutes(5), ct))
            return new FailedLoginRequestPreview(LoginRequestError.RATE_LIMITED);

        var record = await ReadAsync(token, ct);

        if (record is null)
            return new FailedLoginRequestPreview(LoginRequestError.NOT_FOUND);

        if (record.Status != QrLoginStatus.Pending)
            return new FailedLoginRequestPreview(LoginRequestError.ALREADY_USED);

        return new SuccessLoginRequestPreview(new LoginRequestPreview(
            record.ClientName,
            record.HostName,
            record.Ip,
            record.Region,
            record.CreatedAt,
            record.ExpiresAt));
    }

    public async Task<IApproveLoginRequestResult> ApproveAsync(string token, Guid userId, CancellationToken ct = default)
    {
        if (!await AllowAsync($"rl:qr:approve:user:{userId}", max: 30, TimeSpan.FromMinutes(5), ct))
            return new FailedApproveLoginRequest(LoginRequestError.RATE_LIMITED);

        var record = await ReadAsync(token, ct);

        if (record is null)
            return new FailedApproveLoginRequest(LoginRequestError.NOT_FOUND);

        if (record.Status != QrLoginStatus.Pending)
            return new FailedApproveLoginRequest(LoginRequestError.ALREADY_USED);

        try
        {
            // Bound to the machine that asked for the code, not to the phone's. Every access token in
            // the platform carries an `mh` claim that TokenAuthorization checks against the caller's
            // machine cookie, so a token minted here is only usable from that browser even if the
            // record leaked in the seconds it exists.
            var issued = await userManager.GenerateJwt(userId, record.MachineId, ["argon.app"]);

            record.Status       = QrLoginStatus.Approved;
            record.UserId       = userId;
            record.Token        = issued.token;
            record.RefreshToken = issued.refreshToken;

            await cache.StringSetAsync(Key(token), JsonConvert.SerializeObject(record), ApprovedTtl, ct);

            return new SuccessApproveLoginRequest();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to approve QR login request for user {UserId}", userId);
            return new FailedApproveLoginRequest(LoginRequestError.INTERNAL_ERROR);
        }
    }

    public async Task<IRejectLoginRequestResult> RejectAsync(string token, Guid userId, CancellationToken ct = default)
    {
        if (!await AllowAsync($"rl:qr:reject:user:{userId}", max: 30, TimeSpan.FromMinutes(5), ct))
            return new FailedRejectLoginRequest(LoginRequestError.RATE_LIMITED);

        var record = await ReadAsync(token, ct);

        if (record is null)
            return new FailedRejectLoginRequest(LoginRequestError.NOT_FOUND);

        if (record.Status == QrLoginStatus.Approved)
            return new FailedRejectLoginRequest(LoginRequestError.ALREADY_USED);

        record.Status = QrLoginStatus.Rejected;

        await cache.StringSetAsync(Key(token), JsonConvert.SerializeObject(record), RejectedTtl, ct);

        logger.LogInformation("QR login request rejected by user {UserId} from {Ip}", userId, record.Ip);

        return new SuccessRejectLoginRequest();
    }

    /// <summary>
    /// Reads a record, treating an expired one as absent.
    /// </summary>
    /// <remarks>
    /// The cache TTL already removes it, but the record carries its own <c>ExpiresAt</c> and that is
    /// the one the answer is given from: a key whose expiry the store has not yet acted on must not
    /// widen the window by even the eviction lag.
    /// </remarks>
    private async Task<QrLoginRecord?> ReadAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
            return null;

        var json = await cache.StringGetAsync(Key(token), ct);

        if (string.IsNullOrEmpty(json))
            return null;

        var record = JsonConvert.DeserializeObject<QrLoginRecord>(json);

        if (record is null)
            return null;

        if (record.Status == QrLoginStatus.Pending && record.ExpiresAt <= DateTime.UtcNow)
        {
            await cache.KeyDeleteAsync(Key(token), ct);
            return null;
        }

        return record;
    }

    /// <summary>
    /// 192 bits of CSPRNG output, lowercase hex.
    /// </summary>
    /// <remarks>
    /// Hex rather than base64 because the token is drawn as a QR and typed by hand when the camera is
    /// refused: hex stays inside the alphanumeric QR mode (denser symbol, easier read at an angle)
    /// and has no case or <c>+/=</c> to get wrong at a keyboard. The width is set by the fact that
    /// PreviewLoginRequest is a guessing oracle for anyone with an account — the per-user throttle
    /// bounds the rate, the entropy makes the rate irrelevant.
    /// </remarks>
    private static string MintToken()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    /// <summary>
    /// Sliding-window counter, shaped exactly like the one in <c>IdentityInteraction</c>.
    /// </summary>
    /// <remarks>
    /// Fails open for the same reason it does there — the InMemory cache does not implement INCR, and
    /// a cache incident must not become an outage of the sign-in surface. The throttles here are a
    /// nuisance-limiter on top of the token's entropy, not the thing holding the door shut.
    /// </remarks>
    private async Task<bool> AllowAsync(string key, int max, TimeSpan window, CancellationToken ct)
    {
        try
        {
            var count = await cache.StringIncrementAsync(key, ct);

            if (count == 1)
                await cache.KeyExpireAsync(key, window, ct);

            return count <= max;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "QR login rate-limit cache call failed; allowing (fail-open)");
            return true;
        }
    }
}
