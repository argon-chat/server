namespace Argon.Features.Clustering;

/// <summary>
/// Passed to <see cref="IArgonFeature.Configure"/>. Carries the builder plus the role that enabled
/// the feature, so a feature shared by several roles can still adapt to the one it is running in
/// without reaching for environment variables.
/// </summary>
public sealed class ArgonFeatureContext(WebApplicationBuilder builder, RoleDescriptor role)
{
    public WebApplicationBuilder Builder       => builder;
    public IServiceCollection    Services      => builder.Services;
    public IConfiguration        Configuration => builder.Configuration;
    public IHostEnvironment      Environment   => builder.Environment;
    public RoleDescriptor        Role          => role;

    public bool IsClient => role.IsClient;
    public bool IsSilo   => !role.IsClient;
}

/// <summary>
/// Passed to <see cref="IArgonFeature.Map"/>, in the same topological order as
/// <see cref="IArgonFeature.Configure"/>.
/// </summary>
public sealed class ArgonEndpointContext(WebApplication app, RoleDescriptor role)
{
    public WebApplication   App         => app;
    public IHostEnvironment Environment => app.Environment;
    public RoleDescriptor   Role        => role;

    public bool IsClient => role.IsClient;
    public bool IsSilo   => !role.IsClient;
}
