namespace Argon.Features.Pex;

public static class PexFeature
{
    public static IServiceCollection AddArgonPermissions(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IPermissionProvider, ArgonPermissionProvider>();
        return builder.Services;
    }
}

public interface IPermissionProvider
{
    public ValueTask<bool> CanAccess(ArgonEntitlement scope, Guid userId, Guid spaceId);
}

public class NullPermissionProvider : IPermissionProvider
{
    public ValueTask<bool> CanAccess(ArgonEntitlement scope, Guid userId, Guid spaceId)
        => new(true);
}

public class ArgonPermissionProvider(
    IDbContextFactory<ApplicationDbContext> context) : IPermissionProvider
{
    public async ValueTask<bool> CanAccess(ArgonEntitlement scope, Guid userId, Guid spaceId)
    {
        // todo cache
        await using var ctx = await context.CreateDbContextAsync();
        var serverMember = await ctx.UsersToServerRelations
           .Include(sm => sm.SpaceMemberArchetypes)
           .ThenInclude(smr => smr.Archetype)
           .FirstOrDefaultAsync(sm => sm.SpaceId == spaceId && sm.UserId == userId);

        if (serverMember is null)
            return false;

        var basePermissions = EntitlementEvaluator.GetBasePermissions(serverMember);

        return (basePermissions & scope) == scope;
    }
}