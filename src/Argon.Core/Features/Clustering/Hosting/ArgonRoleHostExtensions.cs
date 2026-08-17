namespace Argon.Features.Clustering;

using Orleans.Configuration;

/// <summary>
/// Wires a resolved role into the host: registers it for injection, runs the role's features in
/// topological order, and turns the silo into a heterogeneous one.
/// </summary>
public static class ArgonRoleHostExtensions
{
    /// <summary>
    /// Resolves the role from <c>--role</c>, publishes it to DI and runs every enabled feature's
    /// <see cref="IArgonFeature.Configure"/> in topological order.
    /// </summary>
    /// <summary>
    /// Configuration key the role can be named by when there is no command line to read — the
    /// in-process test host, which boots the entry point through <c>WebApplicationFactory</c> and
    /// never gets to pass arguments.
    /// </summary>
    public const string RoleConfigurationKey = "Argon:Role";

    public static RoleDescriptor AddArgonRole(this WebApplicationBuilder builder, string[] args)
    {
        var parsed  = ArgonClusterArgs.Parse(args);
        var catalog = ArgonClusterCatalog.Build();

        var selected = parsed.Role
                    ?? (builder.Configuration[RoleConfigurationKey] is { Length: > 0 } configured
                            ? new ArgonRoleId(configured)
                            : throw new InvalidOperationException(
                                  $"No role selected. Start the server with --role <name>, set " +
                                  $"'{RoleConfigurationKey}' in configuration, or run --roles to list what is available."));

        var role = catalog.Require(selected);

        var fatal = role.Diagnostics.Where(d => d.Severity is ClusterDiagnosticSeverity.Error).ToArray();
        if (fatal.Length > 0)
            throw new InvalidOperationException(
                $"role '{role.Id}' is not startable:{Environment.NewLine}" +
                string.Join(Environment.NewLine, fatal.Select(d => $"  {d}")));

        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(role);

        // Per-feature files first: they are configuration like any other, and everything below —
        // the validation pass, the options registrations, the features themselves — has to see them.
        var configuration = builder.AddFeatureConfiguration(role);

        var report = FeatureConfigurationValidator.Validate(role, builder.Configuration);

        foreach (var diagnostic in configuration.Concat(report.Warnings)
                    .Where(d => d.Severity is ClusterDiagnosticSeverity.Warning))
            Console.Error.WriteLine($"  {diagnostic}");

        var fatalConfiguration = configuration.Concat(report.Errors)
           .Where(d => d.Severity is ClusterDiagnosticSeverity.Error)
           .ToArray();

        if (fatalConfiguration.Length > 0)
            throw new InvalidOperationException(
                $"role '{role.Id}' is misconfigured:{Environment.NewLine}" +
                string.Join(Environment.NewLine, fatalConfiguration.Select(d => $"  {d}")) +
                $"{Environment.NewLine}Run --validate-config --role {role.Id} to check without starting.");

        foreach (var definition in role.Features.Ordered)
        {
            foreach (var binding in definition.Options)
                binding.Register(builder.Services, builder.Configuration);

            var context = new ArgonFeatureContext(builder, role, definition);
            ((IArgonFeature)Activator.CreateInstance(definition.FeatureType)!).Configure(context);
        }

        return role;
    }

    /// <summary>
    /// Runs every enabled feature's <see cref="IArgonFeature.Map"/> in the same order
    /// <see cref="AddArgonRole"/> configured them.
    /// </summary>
    public static WebApplication UseArgonRole(this WebApplication app)
    {
        var role = app.Services.GetRequiredService<RoleDescriptor>();

        foreach (var definition in role.Features.Ordered)
            ((IArgonFeature)Activator.CreateInstance(definition.FeatureType)!)
               .Map(new ArgonEndpointContext(app, role, definition));

        return app;
    }

    /// <summary>
    /// Restricts the silo to the grain classes this role hosts, which is what makes the cluster
    /// heterogeneous.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filters rather than replaces. The Orleans documentation's snippet for heterogeneous silos is
    /// <c>options.Classes.Clear()</c> followed by adding back the wanted types — that does not work.
    /// The default set also contains Orleans' own system grains, and a silo without
    /// <c>ManagementGrain</c> fails to start with
    /// <c>ArgumentException: Could not find an implementation for interface IManagementGrain</c>.
    /// So only <i>our</i> grains are removed, and only the ones this role does not host.
    /// </para>
    /// <para>
    /// <c>GrainTypeOptions.Interfaces</c> is deliberately left intact: the full interface list is
    /// what lets this silo resolve and call grain interfaces hosted by other roles in the cluster.
    /// </para>
    /// </remarks>
    public static ISiloBuilder UseArgonGrainTypes(this ISiloBuilder silo, RoleDescriptor role)
    {
        if (role.IsClient)
            throw new InvalidOperationException(
                $"role '{role.Id}' is a client and hosts no grains; do not configure a silo for it");

        return silo.Configure<GrainTypeOptions>(options =>
        {
            options.Classes.RemoveWhere(type => IsOurs(type) && !role.HostedGrains.Contains(type));

            // A role may host a grain the default scan missed; adding is cheap and idempotent.
            foreach (var grain in role.HostedGrains)
                options.Classes.Add(grain);
        });
    }

    /// <summary>
    /// Whether a grain class is the product's rather than the runtime's. Anything that is not ours
    /// stays in the silo untouched — those are Orleans' system grains and removing them breaks it.
    /// </summary>
    private static bool IsOurs(Type grainClass)
        => grainClass.Assembly.GetName().Name?.StartsWith("Argon", StringComparison.Ordinal) is true;

    /// <summary>
    /// Validates the topology this process belongs to before serving traffic. Opt-in via
    /// <c>ARGON_VALIDATE_ON_BOOT=1</c> — the IL scan costs seconds and is wasted on a healthy
    /// production start, but catches drift immediately in development.
    /// </summary>
    public static WebApplicationBuilder ValidateArgonTopologyOnBoot(
        this WebApplicationBuilder builder,
        string[]                   args,
        ClusterValidationOptions?  options = null)
    {
        if (Environment.GetEnvironmentVariable("ARGON_VALIDATE_ON_BOOT") is not ("1" or "true"))
            return builder;

        var parsed = ArgonClusterArgs.Parse(args);
        if (parsed.Topology is not { } topologyName)
            throw new InvalidOperationException(
                "ARGON_VALIDATE_ON_BOOT is set but no --topology was given; there is nothing to validate against");

        var catalog = ArgonClusterCatalog.Build();
        if (!catalog.Topologies.TryGetValue(topologyName, out var topology))
            throw new InvalidOperationException($"unknown topology '{topologyName}'");

        var index  = GrainTypeIndex.Build(catalog.Scope);
        var report = ClusterValidator.Validate(catalog, topology, index,
            new IlGrainGraphScanner(catalog.Scope, index), options);

        foreach (var diagnostic in report.Warnings)
            Console.Error.WriteLine($"  {diagnostic}");

        if (!report.IsValid)
            throw new InvalidOperationException(
                $"topology '{topologyName}' is invalid:{Environment.NewLine}" +
                string.Join(Environment.NewLine, report.Errors.Select(d => $"  {d}")));

        return builder;
    }
}
