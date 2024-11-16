namespace Argon.Api.Features.Pex;

using ActualLab.Collections;


public static class PexFeature
{
    public static IServiceCollection AddArgonPermissions(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IPermissionProvider, NullPermissionProvider>();
        return builder.Services;
    }
}


public interface IPermissionProvider
{
    public bool CanAccess(string scope, PropertyBag bag);
}

public class NullPermissionProvider : IPermissionProvider
{
    public bool CanAccess(string scope, PropertyBag bag)
        => true;
}