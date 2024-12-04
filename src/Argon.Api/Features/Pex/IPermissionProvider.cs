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
    public ValueTask<bool> CanAccess(ArgonEntitlement scope, Guid userId, Guid serverId);
}

public class NullPermissionProvider : IPermissionProvider
{
    public ValueTask<bool> CanAccess(ArgonEntitlement scope, Guid userId, Guid serverId)
        => new(true);
}

public class ArgonPermissionProvider(ApplicationDbContext ctx) : IPermissionProvider
{
    public async ValueTask<bool> CanAccess(ArgonEntitlement scope, Guid userId, Guid serverId)
    {
        // todo cache
        var serverMember = await ctx.UsersToServerRelations
           .Include(sm => sm.ServerMemberArchetypes)
           .ThenInclude(smr => smr.Archetype)
           .FirstOrDefaultAsync(sm => sm.ServerId == serverId && sm.UserId == userId);

        if (serverMember is null)
            return false;

        var basePermissions = EntitlementEvaluator.GetBasePermissions(serverMember);

        return (basePermissions & scope) == scope;
    }
}