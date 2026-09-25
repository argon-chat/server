namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.Admin;
using ConsoleContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The operator console, with a system operator and a regular one of the fixture's own to act as.
/// </summary>
/// <remarks>
/// <para>The console is driven directly rather than over its Ion port, as <c>AdminConsoleTests</c>
/// does and for the reason given there: the port is guarded by an operator JWT against a live JWKS
/// endpoint, and the interceptor's only output is the ambient <see cref="OperatorRequestContext"/>.</para>
///
/// <para>Every fixture seeds its own pair of operators. Operator management checks the caller against
/// the database, and a fixture that deactivated or enrolled a shared operator would change what its
/// neighbours' operator may do.</para>
/// </remarks>
public abstract class AdminTestBase : TestBase
{
    protected abstract Guid SystemOperatorId  { get; }
    protected abstract Guid RegularOperatorId { get; }

    protected string SystemOperatorEmail  => $"{GetType().Name.ToLowerInvariant()}.system@argon.test";
    protected string RegularOperatorEmail => $"{GetType().Name.ToLowerInvariant()}.regular@argon.test";

    [OneTimeSetUp]
    public async Task SeedOperators()
    {
        await using var db = await NewDbAsync(CancellationToken.None);

        foreach (var (id, email, isSystem) in new[]
                 {
                     (SystemOperatorId, SystemOperatorEmail, true),
                     (RegularOperatorId, RegularOperatorEmail, false)
                 })
        {
            if (await db.Operators.AnyAsync(o => o.Id == id, CancellationToken.None))
                continue;

            db.Operators.Add(new OperatorEntity
            {
                Id               = id,
                DisplayName      = isSystem ? "Coverage System Operator" : "Coverage Regular Operator",
                Email            = email,
                IsActive         = true,
                IsSystemOperator = isSystem
            });
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The console, acting as this fixture's system operator.</summary>
    /// <remarks>
    /// Synchronous for the reason <c>AccountConsoleHarness.Console</c> gives: the context is an
    /// <c>AsyncLocal</c>, and only a synchronous write survives into the caller. The console reads it
    /// on every call, so calling this (or <see cref="AdminAs"/>) again switches who is acting.
    /// </remarks>
    protected (AsyncServiceScope Scope, IAdminConsole Console) Admin()
        => AdminAs(SystemOperatorId, SystemOperatorEmail);

    protected (AsyncServiceScope Scope, IAdminConsole Console) AdminAs(Guid operatorId, string email)
    {
        var scope = FactoryAsp.Services.CreateAsyncScope();

        OperatorRequestContext.Set(new OperatorRequestContextData
        {
            UserId                = Guid.Empty,
            OperatorId            = operatorId,
            Email                 = email,
            CertificateThumbprint = "TEST-THUMBPRINT"
        });

        return (scope, scope.ServiceProvider.GetRequiredService<IAdminConsole>());
    }

    protected async Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

    /// <summary>The audit rows one action left against one target, newest first.</summary>
    protected static async Task<List<AuditEntry>> AuditAsync(IAdminConsole admin, string action, string targetId, CancellationToken ct)
        => (await admin.GetAuditLog(new AuditLogQuery(null, action, targetId, null, null, 0, 50), ct)).entries.Values.ToList();

    /// <summary>A developer team owned by <paramref name="ownerId"/>, with the owner as its one member.</summary>
    protected async Task<Guid> SeedTeamAsync(Guid ownerId, string name, CancellationToken ct)
    {
        var teamId = Guid.NewGuid();

        await using var db = await NewDbAsync(ct);

        db.TeamEntities.Add(new DevTeamEntity { TeamId = teamId, OwnerId = ownerId, Name = name });
        db.MemberTeamEntities.Add(new DevTeamMemberEntity
        {
            TeamId = teamId, UserId = ownerId, JoinedAt = DateTime.UtcNow, IsOwner = true, Claims = ["team:admin"]
        });

        await db.SaveChangesAsync(ct);

        return teamId;
    }

    /// <summary>A plain OAuth application in <paramref name="teamId"/>.</summary>
    protected async Task<(Guid AppId, string ClientId)> SeedAppAsync(Guid teamId, string name, bool isInternal, CancellationToken ct)
    {
        var appId    = Guid.NewGuid();
        var clientId = $"client_{appId:N}";

        await using var db = await NewDbAsync(ct);

        db.AppEntities.Add(new DevAppEntity
        {
            AppId            = appId,
            TeamId           = teamId,
            Name             = name,
            ClientId         = clientId,
            ClientSecret     = Guid.NewGuid().ToString("N"),
            AppType          = DevAppType.Application,
            IsInternalApp    = isInternal,
            RequiredScopes   = [],
            AllowedRedirects = []
        });

        await db.SaveChangesAsync(ct);

        return (appId, clientId);
    }

    /// <summary>A bot in <paramref name="teamId"/>, with the user account it speaks as.</summary>
    protected async Task<(Guid AppId, Guid BotUserId, string Username)> SeedBotAsync(Guid teamId, string name, bool isInternal,
        CancellationToken ct)
    {
        var botUserId = Guid.NewGuid();
        var appId     = Guid.NewGuid();
        var username  = $"cbot_{botUserId:N}"[..24];

        await using var db = await NewDbAsync(ct);

        db.Users.Add(new UserEntity
        {
            Id          = botUserId,
            Username    = username,
            DisplayName = name,
            Email       = $"{username}@test.local",
            AgreeTOS    = true,
            DateOfBirth = new DateOnly(2000, 1, 1)
        });

        db.BotEntities.Add(new BotEntity
        {
            AppId            = appId,
            TeamId           = teamId,
            Name             = name,
            ClientId         = Guid.NewGuid().ToString("N"),
            ClientSecret     = Guid.NewGuid().ToString("N"),
            AppType          = DevAppType.Bot,
            BotToken         = Guid.NewGuid().ToString("N"),
            BotAsUserId      = botUserId,
            LifecycleState   = BotLifecycleState.Development,
            MaxSpaces        = 10,
            IsInternalApp    = isInternal,
            RequiredScopes   = [],
            AllowedRedirects = []
        });

        await db.SaveChangesAsync(ct);

        await db.Users.Where(u => u.Id == botUserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.BotEntityId, appId), ct);

        return (appId, botUserId, username);
    }
}
