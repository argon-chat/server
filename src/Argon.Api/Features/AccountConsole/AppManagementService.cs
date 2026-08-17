namespace Argon.Api.Features.AccountConsole;

using AccountContracts;
using ion.runtime;
using System.Buffers.Binary;
using BotLifecycleState = Argon.Core.Entities.Data.BotLifecycleState;

/// <summary>
/// The applications a dev team owns: creating them, rotating their credentials, and editing what
/// they are allowed to ask for.
/// </summary>
public sealed class AppManagementService(
    ITeamAccessChecker accessChecker,
    IHttpContextAccessor accessor) : IAppManagement
{
    private IDevTeamsGrain Teams => this.GetGrain<IDevTeamsGrain>(Guid.Empty);

    public async Task<AppDetails> CreateBotApp(Guid teamId, string name, string username, CancellationToken ct = default)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);

        // Checked here rather than left to the grain so the client gets the protocol error it knows
        // how to render; the grain keeps its own guard for the race between check and insert.
        if (await Teams.CheckUsernameForBotAsync(username, ct) is not CheckBotUsernameValid.OK)
            throw new IonRequestException(new IonProtocolError("VALIDATION_FAILED", "bad payload"));

        return await Teams.CreateBotAppAsync(teamId, name, username, ct);
    }

    public async Task<AppDetails> CreateClientApp(Guid teamId, string name, ClientAppPlatform platform, CancellationToken ct = default)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);
        return await Teams.CreateClientAppAsync(teamId, name, platform, ct);
    }

    public async Task<AppDetails> GetAppDetails(Guid teamId, Guid appId, CancellationToken ct = default)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);
        return await Teams.GetAppDetailsAsync(teamId, appId, ct);
    }

    public Task<CheckBotUsernameValid> CheckUsernameForBot(Guid teamId, string username, CancellationToken ct = default)
        => Teams.CheckUsernameForBotAsync(username, ct);

    public async Task<string> RegenerateBotToken(Guid teamId, Guid appId, CancellationToken ct = default)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);
        return await Teams.RegenerateBotTokenAsync(teamId, appId, ct);
    }

    public async Task UpdateScope(Guid teamId, Guid appId, ScopeKeyValue scope, CancellationToken ct = default)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);
        await Teams.UpdateScopeAsync(teamId, appId, scope, ct);
    }

    public async Task<AddRedirectResult> AddRedirect(Guid teamId, Guid appId, string redirect, CancellationToken ct = default)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);

        // The validator dials the redirect host to inspect its certificate. That outbound connection
        // is made from the console, which is a client role, so a hostile redirect target never gets
        // to make a silo open a socket.
        if (await CompositeOAuthRedirectValidator.ValidatorForOAuthApps().ValidateAsync(redirect) is { Length: > 0 } error)
            return new AddRedirectResult(false, error);

        return await Teams.AddRedirectAsync(teamId, appId, redirect, ct);
    }

    public async Task RemoveRedirect(Guid teamId, Guid appId, string redirect, CancellationToken ct = default)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);
        await Teams.RemoveRedirectAsync(teamId, appId, redirect, ct);
    }

    public Task UpdateRedirects(Guid teamId, Guid appId, IonArray<string> redirects, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task PublishBot(Guid teamId, Guid appId, CancellationToken ct = default)
        => SetLifecycle(teamId, appId, BotLifecycleState.Published, ct);

    public Task UnpublishBot(Guid teamId, Guid appId, CancellationToken ct = default)
        => SetLifecycle(teamId, appId, BotLifecycleState.Development, ct);

    public Task SuspendBot(Guid teamId, Guid appId, CancellationToken ct = default)
        => SetLifecycle(teamId, appId, BotLifecycleState.Suspended, ct);

    public async Task UpdateBotEntitlements(Guid teamId, Guid appId, ulong entitlements, CancellationToken ct = default)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);
        await Teams.UpdateBotEntitlementsAsync(teamId, appId, (ArgonEntitlement)entitlements, ct);
    }

    public async Task SetBotOAuth(Guid teamId, Guid appId, bool enabled, CancellationToken ct = default)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);
        await Teams.SetBotOAuthAsync(teamId, appId, enabled, ct);
    }

    public Task<IUploadAvatarResult> BeginUploadAppAvatar(Guid teamId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CompleteUploadAppAvatar(Guid teamId, Guid blobId, CancellationToken ct = default)
        => throw new NotImplementedException();

    private async Task SetLifecycle(Guid teamId, Guid appId, BotLifecycleState state, CancellationToken ct)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);
        await Teams.SetBotLifecycleAsync(teamId, appId, state, ct);
    }

    /// <summary>
    /// Issues the browser cookie that lets a developer exercise their own app against the live
    /// domain without going through a full sign-in.
    /// </summary>
    public async Task EnsureCoockiesForApp(Guid teamId, Guid appId, CancellationToken ct = default)
    {
        if (accessor.HttpContext is null)
            throw new InvalidOperationException("HttpContext is not available");

        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);

        var details = await Teams.GetAppDetailsAsync(teamId, appId, ct);

        var userAgent = accessor.HttpContext.Request.Headers.UserAgent.ToString();
        var deviceId  = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.IsNullOrEmpty(userAgent) ? "unknown" : userAgent));

        var (sessionId, unixTime) = GenerateHashSession(deviceId);

        var salt = string.Join("",
            BitConverter.GetBytes(unixTime).Select(x => $"{x:X2}{Random.Shared.Next():X4}").Reverse());

        var key = $"hwid=partial&rum=0&ert={salt}&scid={sessionId}&colt={deviceId}&ner={details.clientId}";

        accessor.HttpContext.Response.Cookies.Append("ArgonSecure", key, new CookieOptions
        {
            Domain   = ".argon.gl",
            Expires  = DateTimeOffset.Now.AddDays(7),
            HttpOnly = true,
            Secure   = true,
            Path     = "/",
            SameSite = SameSiteMode.None
        });
    }

    private static (Guid SessionId, long UnixTime) GenerateHashSession(string deviceId)
    {
        var        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> baseGuid  = stackalloc byte[16];
        Span<byte> timeBytes = stackalloc byte[8];
        Span<byte> result    = stackalloc byte[16];

        BinaryPrimitives.WriteInt64BigEndian(timeBytes, timestamp);

        var textBytes = Encoding.UTF8.GetBytes(deviceId);

        for (var i = 0; i < 16; i++) baseGuid[i] ^= textBytes[i % textBytes.Length];
        for (var i = 0; i < 8; i++) result[i]    =  (byte)(baseGuid[i] ^ timeBytes[i % 8]);
        for (var i = 8; i < 16; i++) result[i]   =  (byte)(~timeBytes[i % 8] ^ baseGuid[i]);

        return (new Guid(result), timestamp);
    }
}
