namespace Argon.Api.Grains;

/// <summary>
/// Implements the IAppsManagementGrain interface for bot/app credential lookup
/// and OAuth consent screen support.
/// Keyed by a static Guid (singleton-style, or can be keyed by teamId for partitioning).
/// </summary>
public sealed class AppsManagementGrain(IServiceProvider serviceProvider, ILogger<AppsManagementGrain> logger) : Grain, IAppsManagementGrain
{
    public async Task<BotCredentialsInfo?> GetCredentialsForBotAsync(string clientId, CancellationToken ct = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var app = await db.AppEntities
           .AsNoTracking()
           .Where(a => a.ClientId == clientId)
           .Select(a => new BotCredentialsInfo(
                a.ClientId,
                a.ClientSecret,
                a.AllowedRedirects,
                a.RequiredScopes,
                true,
                a.AllowMagicLink))
           .FirstOrDefaultAsync(ct);

        return app;
    }

    public async Task<LoginAllowedResult> CanBeLoginForAppAsync(string clientId, Guid userId, CancellationToken ct = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var app = await db.AppEntities
           .AsNoTracking()
           .FirstOrDefaultAsync(a => a.ClientId == clientId, ct);

        if (app is null)
            return new LoginAllowedResult(false, "Application not found");

        // Check if user is blocked or app is restricted
        if (app is Argon.Core.Entities.Data.BotEntity bot && bot.IsRestricted)
            return new LoginAllowedResult(false, "Bot is restricted");

        return new LoginAllowedResult(true, null);
    }

    public async Task<OAuthAppInfo?> GetOAuthAppInfoAsync(string clientId, IReadOnlyList<string> requestedScopes, CancellationToken ct = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var app = await db.AppEntities
           .AsNoTracking()
           .Include(a => a.Team)
           .Where(a => a.ClientId == clientId)
           .Select(a => new
            {
                a.Name,
                a.Description,
                TeamName = a.Team.Name,
            })
           .FirstOrDefaultAsync(ct);

        if (app is null)
            return null;

        // Check verification separately (can't use 'is' in expression tree)
        var isVerified = await db.AppClientEntities
           .AsNoTracking()
           .Where(c => c.ClientId == clientId)
           .Select(c => c.IsVerified)
           .FirstOrDefaultAsync(ct);

        return new OAuthAppInfo(
            app.Name,
            app.Description,
            null,
            app.TeamName,
            null,
            isVerified,
            requestedScopes);
    }
}
