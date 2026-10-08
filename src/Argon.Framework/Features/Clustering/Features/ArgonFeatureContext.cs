namespace Argon.Features.Clustering;

/// <summary>
/// Passed to <see cref="IArgonFeature.Configure"/>. Carries the builder plus the role that enabled
/// the feature, so a feature shared by several roles can still adapt to the one it is running in
/// without reaching for environment variables.
/// </summary>
public sealed class ArgonFeatureContext(
    WebApplicationBuilder                 builder,
    RoleDescriptor                        role,
    FeatureDefinition                     feature,
    ICollection<Action<IIonTransportRegistration>> ionRegistrations)
{
    /// <summary>
    /// Contributes Ion services and interceptors.
    /// </summary>
    /// <remarks>
    /// Not <c>Services.AddIonProtocol</c> directly, because only the first such call's extra ports are
    /// ever bound. Three features register Ion services here and two of them want a port of their own;
    /// the symptom was the admin console listening on nothing while the account console beside it
    /// worked, because the account console's feature happened to configure first.
    /// <para>
    /// Contributions are collected and applied as one call once every feature has configured.
    /// </para>
    /// </remarks>
    public void Ion(Action<IIonTransportRegistration> configure)
        => ionRegistrations.Add(configure);

    public WebApplicationBuilder Builder       => builder;
    public IServiceCollection    Services      => builder.Services;
    public IConfiguration        Configuration => builder.Configuration;
    public IHostEnvironment      Environment   => builder.Environment;
    public RoleDescriptor        Role          => role;
    public FeatureDefinition     Feature       => feature;

    public bool IsClient => role.IsClient;
    public bool IsSilo   => !role.IsClient;

    /// <summary>
    /// The feature's own settings, bound now.
    /// </summary>
    /// <remarks>
    /// Bound directly rather than resolved as <c>IOptions&lt;T&gt;</c> because there is no container
    /// yet — <see cref="IArgonFeature.Configure"/> is what builds it. The values are the same ones
    /// <c>IOptions&lt;T&gt;</c> will hand out afterwards; only the timing differs.
    /// </remarks>
    public TOptions Options<TOptions>() where TOptions : class
        => feature.BindOptions<TOptions>(builder.Configuration);

    /// <summary>
    /// Another feature's declared settings, for the case where two features genuinely act on one
    /// section — the Sentry tunnel is configured from the same block as Sentry itself.
    /// </summary>
    /// <remarks>
    /// Explicit and type-checked, rather than reaching into <c>ctx.Configuration</c> by section name.
    /// A section keeps exactly one owner, which is what the <c>conf.d</c> ownership check depends on.
    /// </remarks>
    public TOptions OptionsOf<TFeature, TOptions>()
        where TFeature : IArgonFeature
        where TOptions : class
        => FeatureCatalog.Describe<TFeature>().BindOptions<TOptions>(builder.Configuration);
}

/// <summary>
/// Passed to <see cref="IArgonFeature.Map"/>, in the same topological order as
/// <see cref="IArgonFeature.Configure"/>.
/// </summary>
public sealed class ArgonEndpointContext(WebApplication app, RoleDescriptor role, FeatureDefinition feature)
{
    public WebApplication   App         => app;
    public IHostEnvironment Environment => app.Environment;
    public RoleDescriptor   Role        => role;
    public FeatureDefinition Feature    => feature;

    public bool IsClient => role.IsClient;
    public bool IsSilo   => !role.IsClient;

    /// <summary>
    /// The feature's own settings. Resolved through <c>IOptions&lt;T&gt;</c> here, because by now the
    /// container exists and going through it is what makes the values the validated ones.
    /// </summary>
    public TOptions Options<TOptions>() where TOptions : class
        => app.Services.GetRequiredService<IOptions<TOptions>>().Value;
}

public static class FeatureDefinitionOptionsExtensions
{
    /// <summary>
    /// Binds one of the feature's declared options classes.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The feature never declared this options type. Asking for settings a feature did not declare is
    /// a mistake in the feature, not a missing configuration file — the section would have no owner,
    /// no validation rule and no <c>conf.d</c> file it is allowed to come from.
    /// </exception>
    public static TOptions BindOptions<TOptions>(this FeatureDefinition feature, IConfiguration configuration)
        where TOptions : class
    {
        var binding = feature.Options.FirstOrDefault(o => o.OptionsType == typeof(TOptions))
                   ?? throw new InvalidOperationException(
                          $"Feature '{feature.Name}' did not declare Options<{typeof(TOptions).Name}>(); " +
                          $"it declares {(feature.Options.Count == 0
                              ? "no configuration"
                              : string.Join(", ", feature.Options.Select(o => o.OptionsType.Name)))}");

        return (TOptions)binding.Bind(configuration);
    }
}
