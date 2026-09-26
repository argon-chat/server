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

    public async Task<IAppDetailsResult> CreateBotApp(Guid teamId, string name, string username, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(teamId, ct))
            return new FailedAppDetails(AppManagementError.NO_PERMISSION);

        // Checked here rather than left to the grain so the client gets an error it knows how to
        // render; the grain keeps its own guard for the race between check and insert.
        if (await Teams.CheckUsernameForBotAsync(username, ct) is not CheckBotUsernameValid.OK)
            return new FailedAppDetails(AppManagementError.INVALID_USERNAME);

        return new SuccessAppDetails(await Teams.CreateBotAppAsync(teamId, name, username, ct));
    }

    public async Task<IAppDetailsResult> CreateClientApp(Guid teamId, string name, ClientAppPlatform platform, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(teamId, ct))
            return new FailedAppDetails(AppManagementError.NO_PERMISSION);

        return new SuccessAppDetails(await Teams.CreateClientAppAsync(teamId, name, platform, ct));
    }

    public async Task<IAppDetailsResult> GetAppDetails(Guid teamId, Guid appId, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(teamId, ct))
            return new FailedAppDetails(AppManagementError.NO_PERMISSION);

        var (error, app) = await Teams.GetAppDetailsAsync(teamId, appId, ct);

        return error is AppManagementError.NONE
            ? new SuccessAppDetails(app!)
            : new FailedAppDetails(error);
    }

    public Task<CheckBotUsernameValid> CheckUsernameForBot(Guid teamId, string username, CancellationToken ct = default)
        => Teams.CheckUsernameForBotAsync(username, ct);

    public async Task<IRegenerateBotTokenResult> RegenerateBotToken(Guid teamId, Guid appId, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(teamId, ct))
            return new FailedRegenerateBotToken(AppManagementError.NO_PERMISSION);

        var (error, token) = await Teams.RegenerateBotTokenAsync(teamId, appId, ct);

        return error is AppManagementError.NONE
            ? new SuccessRegenerateBotToken(token!)
            : new FailedRegenerateBotToken(error);
    }

    public async Task<IAppManagementResult> UpdateScope(Guid teamId, Guid appId, ScopeKeyValue scope, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(teamId, ct))
            return Managed(AppManagementError.NO_PERMISSION);

        return Managed(await Teams.UpdateScopeAsync(teamId, appId, scope, ct));
    }

    public async Task<AddRedirectResult> AddRedirect(Guid teamId, Guid appId, string redirect, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(teamId, ct))
            return new AddRedirectResult(false, "You are not a member of this team.");

        // Which rules apply depends on where the application runs, so the app is read before its
        // redirect is judged. It is also refused when the app is not this team's, which is the same
        // guard the grain applies to the write below.
        if (await Teams.GetAppDetailsAsync(teamId, appId, ct) is not (AppManagementError.NONE, { } app))
            return new AddRedirectResult(false, "App not found.");

        // The validator dials the redirect host to inspect its certificate. That outbound connection
        // is made from the console, which is a client role, so a hostile redirect target never gets
        // to make a silo open a socket.
        if (await ValidatorFor(app).ValidateAsync(redirect) is { Length: > 0 } error)
            return new AddRedirectResult(false, error);

        return await Teams.AddRedirectAsync(teamId, appId, redirect, ct);
    }

    public async Task<IAppManagementResult> RemoveRedirect(Guid teamId, Guid appId, string redirect, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(teamId, ct))
            return Managed(AppManagementError.NO_PERMISSION);

        return Managed(await Teams.RemoveRedirectAsync(teamId, appId, redirect, ct));
    }

    public Task UpdateRedirects(Guid teamId, Guid appId, IonArray<string> redirects, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<IAppManagementResult> PublishBot(Guid teamId, Guid appId, CancellationToken ct = default)
        => SetLifecycle(teamId, appId, BotLifecycleState.Published, ct);

    public Task<IAppManagementResult> UnpublishBot(Guid teamId, Guid appId, CancellationToken ct = default)
        => SetLifecycle(teamId, appId, BotLifecycleState.Development, ct);

    public Task<IAppManagementResult> SuspendBot(Guid teamId, Guid appId, CancellationToken ct = default)
        => SetLifecycle(teamId, appId, BotLifecycleState.Suspended, ct);

    public async Task<IAppManagementResult> UpdateBotEntitlements(Guid teamId, Guid appId, ulong entitlements, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(teamId, ct))
            return Managed(AppManagementError.NO_PERMISSION);

        return Managed(await Teams.UpdateBotEntitlementsAsync(teamId, appId, (ArgonEntitlement)entitlements, ct));
    }

    public async Task<IAppManagementResult> SetBotOAuth(Guid teamId, Guid appId, bool enabled, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(teamId, ct))
            return Managed(AppManagementError.NO_PERMISSION);

        return Managed(await Teams.SetBotOAuthAsync(teamId, appId, enabled, ct));
    }

    public Task<IUploadAvatarResult> BeginUploadAppAvatar(Guid teamId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CompleteUploadAppAvatar(Guid teamId, Guid blobId, CancellationToken ct = default)
        => throw new NotImplementedException();

    /// <summary>
    /// A client app that runs on a device may also come home through a private-use scheme; anything
    /// hosted on the web — a bot, a web-based client app — may not.
    /// </summary>
    private static OAuthRedirectValidator ValidatorFor(AppDetails app)
        => app.clientAppDetails is { platform: not ClientAppPlatform.WebBased }
            ? NativeAppRedirectValidator.ForNativeApps()
            : CompositeOAuthRedirectValidator.ValidatorForOAuthApps();

    private async Task<IAppManagementResult> SetLifecycle(Guid teamId, Guid appId, BotLifecycleState state, CancellationToken ct)
    {
        if (!await IsMemberAsync(teamId, ct))
            return Managed(AppManagementError.NO_PERMISSION);

        return Managed(await Teams.SetBotLifecycleAsync(teamId, appId, state, ct));
    }

    private Task<bool> IsMemberAsync(Guid teamId, CancellationToken ct)
        => accessChecker.IsTeamMemberAsync(this.GetUserId(), teamId, ct);

    private static IAppManagementResult Managed(AppManagementError error)
        => error is AppManagementError.NONE
            ? new SuccessAppManagement()
            : new FailedAppManagement(error);

    /// <summary>
    /// Issues the browser cookie that lets a developer exercise their own app against the live
    /// domain without going through a full sign-in.
    /// </summary>
    public async Task<IAppManagementResult> EnsureCoockiesForApp(Guid teamId, Guid appId, CancellationToken ct = default)
    {
        if (accessor.HttpContext is null)
            throw new InvalidOperationException("HttpContext is not available");

        if (!await IsMemberAsync(teamId, ct))
            return Managed(AppManagementError.NO_PERMISSION);

        if (await Teams.GetAppDetailsAsync(teamId, appId, ct) is not (AppManagementError.NONE, { } details))
            return Managed(AppManagementError.NOT_FOUND);

        var userAgent = accessor.HttpContext.Request.Headers.UserAgent.ToString();
        var deviceId  = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.IsNullOrEmpty(userAgent) ? "unknown" : userAgent));

        var (sessionId, unixTime) = GenerateHashSession(deviceId);

        var salt = string.Join("",
            BitConverter.GetBytes(unixTime).Select(x => $"{x:X2}{Random.Shared.Next():X4}").Reverse());

        var key = $"hwid=partial&rum=0&ert={salt}&scid={sessionId}&colt={deviceId}&ner={details.clientId}";

        accessor.HttpContext.Response.Cookies.Append("ArgonSecure", key, new CookieOptions
        {
            Domain   = ".argon.gl",
            Expires  = DateTimeOffset.UtcNow.AddDays(7),
            HttpOnly = true,
            Secure   = true,
            Path     = "/",
            SameSite = SameSiteMode.None
        });

        return Managed(AppManagementError.NONE);
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
