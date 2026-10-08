namespace Argon.Features.Clustering;

/// <summary>
/// Assembles the configuration a role would start with, without starting it.
/// </summary>
/// <remarks>
/// Exists so <c>--validate-config</c> checks the real thing: the same files in the same order the
/// host uses, per-feature files included. Run inside a container against its mounted
/// <c>conf.d</c> and the answer is what that container will do.
/// <para>
/// The command line is deliberately not a source here. The arguments in hand are the diagnostic
/// command's own, not the ones the server would be started with.
/// </para>
/// </remarks>
public static class FeatureConfigurationProbe
{
    public static FeatureConfigurationProbeResult Build(RoleDescriptor role, string? contentRoot = null)
    {
        var root  = contentRoot ?? Directory.GetCurrentDirectory();
        var found = FeatureConfigurationSources.Discover(role, root);

        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                       ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                       ?? "Production";

        var builder = new ConfigurationBuilder()
           .SetBasePath(root)
           .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
           .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false);

        foreach (var source in found.Sources)
            builder.Add(source);

        builder.AddEnvironmentVariables();

        return new FeatureConfigurationProbeResult
        {
            Environment   = environment,
            Configuration = builder.Build(),
            Diagnostics   = found.Diagnostics
        };
    }
}

public sealed class FeatureConfigurationProbeResult
{
    public required string                          Environment   { get; init; }
    public required IConfiguration                  Configuration { get; init; }
    public required IReadOnlyList<ClusterDiagnostic> Diagnostics   { get; init; }
}
