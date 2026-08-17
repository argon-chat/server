namespace Argon.Api.Grains;

using Orleans.Concurrency;

/// <summary>
/// Who may sign into which application, and what the OAuth provider is told about it.
/// </summary>
/// <remarks>
/// The data lives on <see cref="IDevTeamsGrain"/>; what lives here is the policy — an internal app
/// is for team members with an @argon.gl address, an app that is neither public nor verified is for
/// its own team, and everything else is open. Both grains sit on the same role, so the hop between
/// them stays inside the silo.
/// </remarks>
[StatelessWorker]
public sealed class AppsManagementGrain(ILogger<AppsManagementGrain> logger) : Grain, IAppsManagementGrain
{
    private const string ArgonEmailDomain = "@argon.gl";

    private IDevTeamsGrain Teams => GrainFactory.GetGrain<IDevTeamsGrain>(Guid.Empty);

    public async Task<BotCredentialsInfo?> GetCredentialsForBotAsync(string clientId, CancellationToken ct = default)
    {
        var credentials = await Teams.GetBotCredentialsAsync(clientId, ct);

        if (credentials is null)
            return null;

        logger.LogDebug("Resolved bot credentials for {ClientId}: scopes [{Scopes}], redirects [{Redirects}]",
            clientId, string.Join(',', credentials.scopes), string.Join(',', credentials.allowedRedirects));

        return credentials;
    }

    public async Task<LoginAllowedResult> CanBeLoginForAppAsync(string clientId, Guid userId, CancellationToken ct = default)
    {
        var app = await Teams.GetAppLoginCheckInfoAsync(clientId, ct);

        if (app is null)
            return new LoginAllowedResult(false, "App not found");

        if (!app.IsInternalApp && (app.IsPublic || app.IsVerified))
            return new LoginAllowedResult(true, null);

        var isTeamMember = await Teams.IsUserInTeamAsync(userId, app.TeamId, ct);

        if (!app.IsInternalApp)
        {
            return isTeamMember
                ? new LoginAllowedResult(true, null)
                : new LoginAllowedResult(false, "Unapproved apps require team membership");
        }

        if (!isTeamMember)
            return new LoginAllowedResult(false, "Internal apps require team membership");

        // Membership alone is not enough for an internal app: the account also has to be a staff
        // mailbox, so an invited outside collaborator on the team cannot reach internal tooling.
        var email = await Teams.GetUserEmailAsync(userId, ct);

        return string.IsNullOrEmpty(email) || !email.EndsWith(ArgonEmailDomain, StringComparison.OrdinalIgnoreCase)
            ? new LoginAllowedResult(false, "TenantId does not meet the security requirements")
            : new LoginAllowedResult(true, null);
    }

    public async Task<OAuthAppInfo?> GetOAuthAppInfoAsync(
        string clientId, IReadOnlyList<string> requestedScopes, CancellationToken ct = default)
    {
        var app = await Teams.GetAppOAuthDisplayInfoAsync(clientId, ct);

        if (app is null)
            return null;

        var teamName = await Teams.GetTeamNameAsync(app.TeamId, ct) ?? "Unknown Developer";

        return new OAuthAppInfo(
            app.AppId,
            app.AppName,
            app.Description,
            app.AvatarFileId,
            teamName,
            app.WebsiteUrl,
            app.IsVerified,
            app.IsInternalApp,
            requestedScopes);
    }
}
