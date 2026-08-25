namespace Argon.Grains;

using Argon.Features.Clustering.Regions;

using AccountContracts;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Grains.Interfaces;
using ion.runtime;
using Orleans.Concurrency;
using System.Buffers.Text;
using BotDetails = AccountContracts.BotDetails;
using BotLifecycleState = Argon.Core.Entities.Data.BotLifecycleState;

/// <summary>
/// The developer account console's data access: dev teams, their invites, and the applications they
/// own. Stateless — key it with <see cref="Guid.Empty"/>.
/// </summary>
[StatelessWorker]
public sealed class DevTeamsGrain(IDbContextFactory<ApplicationDbContext> contextFactory) : Grain, IDevTeamsGrain
{
    // ── teams ────────────────────────────────────────────────────────────────────────────────

    public async Task<List<TeamShortDetails>> GetMyTeamsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.TeamEntities
           .AsNoTracking()
           .Where(t => t.Members.Any(m => m.UserId == userId))
           .Select(t => new TeamShortDetails(
                t.TeamId,
                t.Name,
                t.AvatarFileId ?? string.Empty,
                t.Applications.Count))
           .ToListAsync(ct);
    }

    public async Task<TeamDetails> GetTeamDetailsAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var team = await db.TeamEntities
                      .AsNoTracking()
                      .Include(t => t.Members).ThenInclude(m => m.User)
                      .Include(t => t.Applications)
                      .FirstOrDefaultAsync(t => t.TeamId == teamId, ct)
                   ?? throw new InvalidOperationException("Team not found.");

        var members = team.Members.Select(m =>
            new TeamMemberDetails(
                user: new ShortUserDetails(m.UserId, m.User.AvatarFileId ?? "", m.User.DisplayName, m.User.Username),
                teamId: team.TeamId,
                isPending: m.IsPending,
                isOwner: m.IsOwner,
                claims: new IonArray<string>(m.Claims.ToArray()),
                joinedAt: m.JoinedAt)).ToArray();

        var apps = team.Applications.Select(a =>
            new AppDetails(
                a.AppId,
                a.TeamId,
                a.Name,
                a.Description,
                null,
                null,
                a.AppType switch
                {
                    DevAppType.Bot    => AppKind.BotApp,
                    DevAppType.WebApp => AppKind.WebApp,
                    _                 => AppKind.ClientApp
                },
                a.ClientId,
                a.ClientSecret,
                a.VerificationKey,
                a.CreatedAt.Date,
                new IonArray<ScopeKeyValue>([]),
                new IonArray<string>(a.AllowedRedirects))).ToArray();

        return new TeamDetails(
            team.TeamId,
            team.OwnerId,
            team.Name,
            team.AvatarFileId ?? "",
            new IonArray<TeamMemberDetails>(members),
            new IonArray<AppDetails>(apps));
    }

    public async Task<string?> GetTeamNameAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.TeamEntities
           .AsNoTracking()
           .Where(t => t.TeamId == teamId)
           .Select(t => t.Name)
           .FirstOrDefaultAsync(ct);
    }

    public async Task<TeamDetails> CreateTeamAsync(Guid ownerId, string name, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var team = new DevTeamEntity
        {
            TeamId    = ArgonId.New(),
            OwnerId   = ownerId,
            Name      = name,
            CreatedAt = DateTime.UtcNow
        };

        db.Add(team);

        var member = new DevTeamMemberEntity
        {
            TeamId    = team.TeamId,
            UserId    = ownerId,
            IsOwner   = true,
            IsPending = false,
            JoinedAt  = DateTime.UtcNow
        };

        db.Add(member);
        await db.SaveChangesAsync(ct);

        return new TeamDetails(
            team.TeamId,
            team.OwnerId,
            team.Name,
            team.AvatarFileId ?? string.Empty,
            new IonArray<TeamMemberDetails>([]),
            new IonArray<AppDetails>([]));
    }

    public async Task<bool> IsUserInTeamAsync(Guid userId, Guid teamId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.MemberTeamEntities
           .AsNoTracking()
           .AnyAsync(m => m.TeamId == teamId && m.UserId == userId && !m.IsPending, ct);
    }

    public async Task<bool> IsUserTeamOwnerAsync(Guid userId, Guid teamId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.MemberTeamEntities
           .AsNoTracking()
           .AnyAsync(m => m.TeamId == teamId && m.UserId == userId && m.IsOwner, ct);
    }

    public async Task<string?> GetUserEmailAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.Users
           .AsNoTracking()
           .Where(u => u.Id == userId)
           .Select(u => u.Email)
           .FirstOrDefaultAsync(ct);
    }

    // ── invites ──────────────────────────────────────────────────────────────────────────────

    public async Task<List<TeamInviteInfo>> GetTeamInvitesAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.TeamInvites
           .AsNoTracking()
           .Where(x => x.TeamId == teamId && !x.Revoked && !x.Accepted && x.ExpireAt > DateTimeOffset.UtcNow)
           .Join(db.Users,
                inv => inv.FromUserId,
                user => user.Id,
                (inv, fromUser) => new
                {
                    inv,
                    fromUser
                })
           .Join(db.Users,
                tmp => tmp.inv.ToUserId,
                toUser => toUser.Id,
                (tmp, toUser) => new TeamInviteInfo(
                    new ShortUserDetails(tmp.fromUser.Id, tmp.fromUser.AvatarFileId ?? "", tmp.fromUser.DisplayName, tmp.fromUser.Username),
                    new ShortUserDetails(toUser.Id, toUser.AvatarFileId ?? "", toUser.DisplayName, toUser.Username),
                    tmp.inv.CreatedAt))
           .ToListAsync(ct);
    }

    public async Task<List<MyInvitesInfo>> GetMyInvitesAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.TeamInvites
           .AsNoTracking()
           .Where(x => x.ToUserId == userId && !x.Accepted && !x.Revoked && x.ExpireAt > DateTimeOffset.UtcNow)
           .Join(db.Users,
                inv => inv.FromUserId,
                user => user.Id,
                (inv, fromUser) => new
                {
                    inv,
                    fromUser
                })
           .Join(db.TeamEntities,
                tmp => tmp.inv.TeamId,
                team => team.TeamId,
                (tmp, team) => new MyInvitesInfo(
                    new ShortUserDetails(tmp.fromUser.Id,
                        tmp.fromUser.AvatarFileId ?? "", tmp.fromUser.DisplayName, tmp.fromUser.Username),
                    tmp.inv.CreatedAt,
                    new TeamShortDetails(team.TeamId, team.Name,
                        team.AvatarFileId ?? "", team.Applications.Count)))
           .ToListAsync(ct);
    }

    public async Task<InviteUserError> InviteUserToTeamAsync(
        Guid teamId, Guid fromUserId, string username, TimeSpan ttl, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var normalized = username.ToLowerInvariant();
        var user       = await db.Users.FirstOrDefaultAsync(x => x.NormalizedUsername == normalized, ct);

        if (user is null)
            return InviteUserError.USER_NOT_FOUND;

        var existing = await db.TeamInvites
           .FirstOrDefaultAsync(x => x.TeamId == teamId && x.ToUserId == user.Id && !x.Revoked && !x.Accepted, ct);

        if (existing != null)
            return InviteUserError.ALREADY_INVITED;

        var alreadyMember = await db.MemberTeamEntities.AnyAsync(x => x.TeamId == teamId && x.UserId == user.Id, ct);

        if (alreadyMember)
            return InviteUserError.ALREADY_IN_TEAM;

        db.Add(new DevTeamMemberInvite
        {
            TeamId     = teamId,
            FromUserId = fromUserId,
            ToUserId   = user.Id,
            ExpireAt   = DateTimeOffset.UtcNow.Add(ttl),
            CreatedAt  = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);
        return InviteUserError.OK;
    }

    public async Task AcceptInviteAsync(Guid userId, Guid teamId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var invite = await db.TeamInvites
               .FirstOrDefaultAsync(x =>
                        x.TeamId == teamId &&
                        x.ToUserId == userId &&
                        !x.Revoked &&
                        !x.Accepted &&
                        x.ExpireAt > DateTimeOffset.UtcNow,
                    ct);

            if (invite is null)
                throw new InvalidOperationException("Invite not found or expired.");

            invite.Accepted = true;

            var alreadyMember = await db.MemberTeamEntities
               .AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct);

            if (alreadyMember)
            {
                invite.Revoked = true;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return;
            }

            db.Add(new DevTeamMemberEntity
            {
                TeamId    = teamId,
                UserId    = userId,
                IsOwner   = false,
                IsPending = false,
                Claims    = [],
                JoinedAt  = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    public async Task DeclineTeamInviteAsync(Guid userId, Guid teamId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var invite = await db.TeamInvites
           .FirstOrDefaultAsync(x =>
                    x.TeamId == teamId &&
                    x.ToUserId == userId &&
                    !x.Accepted &&
                    !x.Revoked &&
                    x.ExpireAt > DateTimeOffset.UtcNow,
                ct);

        if (invite is null)
            return;

        invite.Revoked = true;

        await db.SaveChangesAsync(ct);
    }

    // ── applications ─────────────────────────────────────────────────────────────────────────

    public async Task<AppDetails> CreateBotAppAsync(Guid teamId, string name, string username, CancellationToken ct = default)
    {
        var normalized = username.ToLowerInvariant();

        if (!normalized.EndsWith("bot"))
            throw new InvalidOperationException("Bot usernames must end in 'bot'.");

        await using var db = await contextFactory.CreateDbContextAsync(ct);

        if (await db.Users.AnyAsync(x => x.NormalizedUsername == normalized, ct))
            throw new InvalidOperationException("Username is already claimed.");

        var now   = DateTime.UtcNow;
        var botId = ArgonId.New();

        var botUser = new UserEntity
        {
            Id          = botId,
            Username    = username,
            DisplayName = name,
            Email       = $"{botId}+{username}@noreply.argon.gl",
            AgreeTOS    = false,
            CreatedAt   = now
        };

        var clientId     = GenerateClientId(botId);
        var clientSecret = GenerateClientSecret(clientId, botId);

        var botEntity = new BotEntity
        {
            AppId                = botId,
            BotAsUser            = botUser,
            BotAsUserId          = botUser.Id,
            CreatedAt            = now,
            RequiresOAuth2       = true,
            BotToken             = GenerateBotToken(botId),
            Name                 = name,
            AllowDMs             = false,
            IsVerified           = false,
            LifecycleState       = BotLifecycleState.Development,
            RequiredEntitlements = ArgonEntitlementKit.Base,
            MaxSpaces            = 5,
            AppType              = DevAppType.Bot,
            ClientId             = clientId,
            ClientSecret         = clientSecret,
            Description          = "",
            VerificationKey      = GenerateVerificationKey(botId),
            UpdatedAt            = now,
            TeamId               = teamId,
            RequiredScopes       = [],
            AllowedRedirects     = []
        };

        db.Add(botUser);
        db.Add(botEntity);
        db.Add(new UserProfileEntity
        {
            UserId    = botId,
            Badges    = [],
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);

        return new AppDetails(
            botEntity.AppId,
            botEntity.TeamId,
            botEntity.Name,
            botEntity.Description,
            MapBot(botEntity),
            null,
            AppKind.BotApp,
            botEntity.ClientId,
            botEntity.ClientSecret,
            botEntity.VerificationKey,
            now,
            new IonArray<ScopeKeyValue>(AvailableScopesFor(botEntity)),
            new IonArray<string>([]));
    }

    public async Task<AppDetails> CreateClientAppAsync(
        Guid teamId, string name, ClientAppPlatform platform, CancellationToken ct = default)
    {
        var now          = DateTime.UtcNow;
        var appId        = ArgonId.New();
        var clientId     = GenerateClientId(appId);
        var clientSecret = GenerateClientSecret(clientId, appId);

        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var appEntity = new ClientAppEntity
        {
            AppId              = appId,
            CreatedAt          = now,
            Name               = name,
            IsVerified         = false,
            IsPublic           = false,
            AppType            = DevAppType.Application,
            ClientId           = clientId,
            ClientSecret       = clientSecret,
            Description        = "",
            VerificationKey    = GenerateVerificationKey(appId),
            UpdatedAt          = now,
            TeamId             = teamId,
            RequiredScopes     = [],
            AllowedRedirects   = [],
            Platform           = (ClientAppPlatformKind)(int)platform,
            RateLimitPerMinute = 120,
            RepositoryUrl      = null,
            WebsiteUrl         = null,
            IsInternalApp      = false
        };

        db.Add(appEntity);
        await db.SaveChangesAsync(ct);

        return new AppDetails(
            appEntity.AppId,
            appEntity.TeamId,
            appEntity.Name,
            appEntity.Description,
            null,
            MapClientApp(appEntity),
            AppKind.ClientApp,
            appEntity.ClientId,
            appEntity.ClientSecret,
            appEntity.VerificationKey,
            now,
            IonArray<ScopeKeyValue>.Empty,
            IonArray<string>.Empty);
    }

    public async Task<CheckBotUsernameValid> CheckUsernameForBotAsync(string username, CancellationToken ct = default)
    {
        var normalized = username.ToLowerInvariant();

        if (!normalized.EndsWith("bot"))
            return CheckBotUsernameValid.POSTFIX_BOT_REQUIRED;

        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.Users.AnyAsync(x => x.NormalizedUsername == normalized, ct)
            ? CheckBotUsernameValid.ALREADY_CLAIMED
            : CheckBotUsernameValid.OK;
    }

    public async Task<AppDetails> GetAppDetailsAsync(Guid teamId, Guid appId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var appInfo = await db.AppEntities.AsNoTracking()
                         .FirstOrDefaultAsync(x => x.TeamId == teamId && x.AppId == appId, ct)
                      ?? throw new InvalidOperationException("App not found.");

        if (appInfo.AppType is DevAppType.Bot)
        {
            var botEntity = await db.BotEntities
               .AsNoTracking()
               .Include(x => x.BotAsUser)
               .ThenInclude(x => x.Profile)
               .FirstAsync(x => x.AppId == appId, ct);

            return Describe(botEntity);
        }

        if (appInfo.AppType is DevAppType.Application)
        {
            var appEntity = await db.AppClientEntities
               .AsNoTracking()
               .FirstAsync(x => x.AppId == appId, ct);

            return new AppDetails(
                appEntity.AppId,
                appEntity.TeamId,
                appEntity.Name,
                appEntity.Description,
                null,
                MapClientApp(appEntity),
                AppKind.ClientApp,
                appEntity.ClientId,
                appEntity.ClientSecret,
                appEntity.VerificationKey,
                appEntity.CreatedAt.Date,
                IonArray<ScopeKeyValue>.Empty,
                new IonArray<string>(appEntity.AllowedRedirects));
        }

        throw new NotSupportedException($"App type {appInfo.AppType} is not supported.");
    }

    public async Task<AppDetails?> GetAppDetailsByClientIdAsync(string clientId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var botEntity = await db.BotEntities
           .AsNoTracking()
           .Include(x => x.BotAsUser)
           .ThenInclude(x => x.Profile)
           .FirstOrDefaultAsync(x => x.ClientId == clientId, ct);

        return botEntity is null ? null : Describe(botEntity);
    }

    public async Task<HashSet<string>> GetAllAllowedOriginsAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var redirects = await db.Set<DevAppEntity>()
           .AsNoTracking()
           .Where(a => a.AllowedRedirects.Count > 0)
           .Select(a => a.AllowedRedirects)
           .ToListAsync(ct);

        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A redirect is a full URL; what CORS compares against is only its scheme, host and port.
        foreach (var redirect in redirects.SelectMany(list => list))
        {
            if (Uri.TryCreate(redirect, UriKind.Absolute, out var uri))
                origins.Add(uri.GetLeftPart(UriPartial.Authority));
        }

        return origins;
    }

    public async Task<AppLoginCheckInfo?> GetAppLoginCheckInfoAsync(string clientId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var clientApp = await db.AppClientEntities
           .AsNoTracking()
           .FirstOrDefaultAsync(x => x.ClientId == clientId, ct);

        if (clientApp is not null)
            return new AppLoginCheckInfo(clientApp.AppId, clientApp.TeamId, clientApp.IsInternalApp, clientApp.IsPublic, clientApp.IsVerified);

        var botApp = await db.BotEntities
           .AsNoTracking()
           .FirstOrDefaultAsync(x => x.ClientId == clientId, ct);

        return botApp is null
            ? null
            : new AppLoginCheckInfo(botApp.AppId, botApp.TeamId, IsInternalApp: false, botApp.IsPublic, botApp.IsVerified);
    }

    public async Task<BotCredentialsInfo?> GetBotCredentialsAsync(string clientId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var bot = await db.BotEntities
           .AsNoTracking()
           .Include(x => x.BotAsUser)
           .ThenInclude(x => x.Profile)
           .FirstOrDefaultAsync(x => x.ClientId == clientId, ct);

        if (bot is null || string.IsNullOrEmpty(bot.ClientSecret))
            return null;

        var scopes = AvailableScopesFor(bot)
           .Where(x => x is { isLocked: false, isRequired: true })
           .Select(x => x.key)
           .ToList();

        return new BotCredentialsInfo(
            bot.ClientId,
            bot.ClientSecret,
            bot.AllowedRedirects,
            scopes,
            scopes.Contains("offline_access"),
            bot.AllowMagicLink);
    }

    public async Task<AppOAuthDisplayInfo?> GetAppOAuthDisplayInfoAsync(string clientId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var clientApp = await db.AppClientEntities
           .AsNoTracking()
           .FirstOrDefaultAsync(x => x.ClientId == clientId, ct);

        if (clientApp is not null)
        {
            return new AppOAuthDisplayInfo(
                clientApp.AppId,
                clientApp.Name,
                clientApp.Description,
                AvatarFileId: null,
                clientApp.WebsiteUrl,
                clientApp.IsVerified,
                clientApp.IsInternalApp,
                clientApp.TeamId);
        }

        var botApp = await db.BotEntities
           .AsNoTracking()
           .Include(x => x.BotAsUser)
           .FirstOrDefaultAsync(x => x.ClientId == clientId, ct);

        return botApp is null
            ? null
            : new AppOAuthDisplayInfo(
                botApp.AppId,
                botApp.Name,
                botApp.Description,
                botApp.BotAsUser?.AvatarFileId,
                WebsiteUrl: null,
                botApp.IsVerified,
                botApp.IsInternalApp,
                botApp.TeamId);
    }

    public async Task<string> RegenerateBotTokenAsync(Guid teamId, Guid appId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var botEntity = await RequireBotAsync(db, teamId, appId, ct);

        botEntity.BotToken = GenerateBotToken(botEntity.AppId);

        await db.SaveChangesAsync(ct);

        return botEntity.BotToken;
    }

    public async Task UpdateScopeAsync(Guid teamId, Guid appId, ScopeKeyValue scope, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var botEntity = await RequireBotAsync(db, teamId, appId, ct);
        var granted   = botEntity.RequiredScopes.Contains(scope.key);

        if (scope.isRequired == granted)
            return;

        if (scope.isRequired)
            botEntity.RequiredScopes.Add(scope.key);
        else
            botEntity.RequiredScopes.Remove(scope.key);

        await db.SaveChangesAsync(ct);
    }

    public async Task<AddRedirectResult> AddRedirectAsync(Guid teamId, Guid appId, string redirect, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var botEntity = await RequireBotAsync(db, teamId, appId, ct);

        if (botEntity.AllowedRedirects.Contains(redirect))
            return new AddRedirectResult(false, "Redirect already exists.");

        botEntity.AllowedRedirects.Add(redirect);

        await db.SaveChangesAsync(ct);

        return new AddRedirectResult(true, null);
    }

    public async Task RemoveRedirectAsync(Guid teamId, Guid appId, string redirect, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var botEntity = await RequireBotAsync(db, teamId, appId, ct);

        if (!botEntity.AllowedRedirects.Remove(redirect))
            return;

        await db.SaveChangesAsync(ct);
    }

    public async Task SetBotLifecycleAsync(Guid teamId, Guid appId, BotLifecycleState state, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var bot = await RequireBotAsync(db, teamId, appId, ct);

        bot.LifecycleState = state;
        bot.UpdatedAt      = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateBotEntitlementsAsync(
        Guid teamId, Guid appId, ArgonEntitlement entitlements, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var bot = await RequireBotAsync(db, teamId, appId, ct);

        bot.RequiredEntitlements = entitlements;
        bot.EntitlementsVersion++;
        bot.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task SetBotOAuthAsync(Guid teamId, Guid appId, bool enabled, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var bot = await RequireBotAsync(db, teamId, appId, ct);

        bot.RequiresOAuth2 = enabled;
        bot.UpdatedAt      = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    // ── mapping ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every bot mutation is scoped by team as well as by app id: the console resolves the team from
    /// the caller's membership, so matching on both is what stops one team from editing another's
    /// bot by guessing an app id.
    /// </summary>
    private static async Task<BotEntity> RequireBotAsync(
        ApplicationDbContext db, Guid teamId, Guid appId, CancellationToken ct)
        => await db.BotEntities.FirstOrDefaultAsync(b => b.AppId == appId && b.TeamId == teamId, ct)
        ?? throw new InvalidOperationException("Bot not found.");

    private static AppDetails Describe(BotEntity bot)
        => new(bot.AppId,
            bot.TeamId,
            bot.Name,
            bot.Description,
            MapBot(bot),
            null,
            AppKind.BotApp,
            bot.ClientId,
            bot.ClientSecret,
            bot.VerificationKey,
            bot.CreatedAt.Date,
            new IonArray<ScopeKeyValue>(AvailableScopesFor(bot)),
            new IonArray<string>(bot.AllowedRedirects));

    private static BotDetails MapBot(BotEntity bot)
        => new(requiresOAuth2: bot.RequiresOAuth2,
            requiresArgxAuth: false,
            allowDMs: bot.AllowDMs,
            isVerfied: bot.IsVerified,
            maxSpaces: bot.MaxSpaces,
            avatarFileId: null,
            botToken: bot.BotToken,
            lifecycleState: (AccountContracts.BotLifecycleState)(int)bot.LifecycleState,
            requiredEntitlements: (ulong)bot.RequiredEntitlements);

    private static ClientAppDetails MapClientApp(ClientAppEntity app)
        => new((ClientAppPlatform)(int)app.Platform,
            app.Platform == ClientAppPlatformKind.WebBased,
            app.IsPublic,
            app.IsVerified,
            app.RepositoryUrl,
            app.RateLimitPerMinute,
            app.WebsiteUrl,
            app.IsInternalApp);

    // ── scopes ───────────────────────────────────────────────────────────────────────────────

    private static readonly string[] ScopesForInternal =
    [
        "role",
        "internal.read",
        "internal.write",
        "infrastructure.read",
        "infrastructure.write",
        "argx.access",
        "argx.read",
        "argx.write"
    ];

    private static readonly string[] ScopesForDefault =
    [
        "identity",
        "user.read",
        "email"
    ];

    private static readonly string[] ScopesForVerified =
    [
        "offline_access"
    ];

    /// <summary>
    /// The scope list the console renders: every scope the bot could ask for, each flagged with
    /// whether it currently asks for it and whether it may be toggled at all. Locking rather than
    /// hiding is deliberate — a developer should see that <c>offline_access</c> exists and that
    /// verification is what unlocks it.
    /// </summary>
    private static List<ScopeKeyValue> AvailableScopesFor(BotEntity bot)
    {
        var scopes = new List<ScopeKeyValue>
        {
            new(true, "openid", true)
        };

        scopes.AddRange(ScopesForDefault.Select(v => new ScopeKeyValue(bot.RequiredScopes.Contains(v), v, false)));
        scopes.AddRange(ScopesForVerified.Select(v => new ScopeKeyValue(bot.RequiredScopes.Contains(v), v, !bot.IsVerified)));

        if (bot is { IsVerified: true, BotAsUser.Profile.Badges: ["staff"] })
            scopes.AddRange(ScopesForInternal.Select(v => new ScopeKeyValue(bot.RequiredScopes.Contains(v), v, false)));

        return scopes;
    }

    // ── credentials ──────────────────────────────────────────────────────────────────────────

    private static string GenerateVerificationKey(Guid appId)
    {
        Span<byte> key = stackalloc byte[32];
        RandomNumberGenerator.Fill(key);

        Span<byte> nonce = stackalloc byte[12];
        RandomNumberGenerator.Fill(nonce);

        Span<byte> plaintext = stackalloc byte[16];
        appId.TryWriteBytes(plaintext);

        Span<byte> ciphertext = stackalloc byte[plaintext.Length];
        Span<byte> tag        = stackalloc byte[16];

        using var cipher = new ChaCha20Poly1305(key);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag);

        Span<byte> output = stackalloc byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(output);
        tag.CopyTo(output[ciphertext.Length..]);

        return Convert.ToHexString(output);
    }

    private static string GenerateClientSecret(string clientId, Guid appId)
    {
        if (!Kmac256.IsSupported)
            throw new PlatformNotSupportedException("KMAC256 not supported on this platform (please install openssl 3.3+)");

        var cidChars   = clientId.AsSpan();
        var cidByteLen = Encoding.UTF8.GetByteCount(cidChars);

        Span<byte> cidBytes = stackalloc byte[cidByteLen];
        Encoding.UTF8.GetBytes(cidChars, cidBytes);

        Span<byte> appBytes = stackalloc byte[16];
        appId.TryWriteBytes(appBytes);

        var message = new byte[cidBytes.Length + appBytes.Length];
        cidBytes.CopyTo(message);
        appBytes.CopyTo(message.AsSpan(cidBytes.Length));

        Span<byte> output = stackalloc byte[32];
        Kmac256.HashData(appBytes, message, output);

        return Convert.ToBase64String(output)
           .Replace('+', '-')
           .Replace('/', '_')
           .TrimEnd('=');
    }

    private static string GenerateClientId(Guid appId)
    {
        if (!Kmac128.IsSupported)
            throw new PlatformNotSupportedException("KMAC128 not supported on this platform (please install openssl 3.3+)");

        Span<byte> appBytes = stackalloc byte[16];
        appId.TryWriteBytes(appBytes);

        Span<byte> output = stackalloc byte[16];
        Kmac128.HashData(appBytes, appBytes, output);

        return Convert.ToHexString(output);
    }

    private static string GenerateBotToken(Guid botId)
    {
        Span<byte> botBytes = stackalloc byte[16];
        botId.TryWriteBytes(botBytes);
        botBytes.Reverse();

        Span<byte> secretBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(secretBytes);

        Span<byte> base64 = stackalloc byte[44];
        Base64.EncodeToUtf8(secretBytes, base64, out _, out var written);

        var secret = Encoding.ASCII.GetString(base64[..written])
           .Replace('+', '-')
           .Replace('/', '_')
           .TrimEnd('=');

        return $"{Convert.ToHexString(botBytes)}:{secret}";
    }
}
