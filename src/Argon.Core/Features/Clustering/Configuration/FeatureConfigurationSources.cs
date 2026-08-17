namespace Argon.Features.Clustering;

using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

/// <summary>
/// The per-feature configuration files a role loads, and the problems found while collecting them.
/// </summary>
public sealed class FeatureConfigurationFiles
{
    public required IReadOnlyList<IConfigurationSource> Sources     { get; init; }
    public required IReadOnlyList<ClusterDiagnostic>    Diagnostics { get; init; }
}

/// <summary>
/// Adds the per-feature configuration files to the host's configuration.
/// </summary>
/// <remarks>
/// Two ways in, and they compose:
/// <list type="bullet">
/// <item><c>conf.d/&lt;feature&gt;.json</c> — one file per feature, holding the sections that feature
/// declared. The content is section-shaped, so a block can be cut out of <c>appsettings.json</c> and
/// pasted in unchanged. A file may only carry sections its own feature declared; anything else is
/// reported as C3 rather than quietly applied.</item>
/// <item><c>ARGON_CONFIG_FILE</c> — one arbitrary document, not scoped to any feature, for the
/// deployment that wants a single mounted override.</item>
/// </list>
/// <para>
/// Precedence, lowest first: <c>appsettings.json</c>, <c>appsettings.{Environment}.json</c>,
/// <c>conf.d/*.json</c>, <c>$ARGON_CONFIG_FILE</c>, environment variables, command line. The image
/// carries the defaults and the mounted file carries the deployment's intent, so a file wins over
/// <c>appsettings</c>; an environment variable still wins over everything, which is what makes a
/// one-off override possible without editing a mount.
/// </para>
/// </remarks>
public static class FeatureConfigurationSources
{
    /// <summary>Overrides the directory scanned for per-feature files.</summary>
    public const string DirectoryVariable = "ARGON_CONFIG_DIR";

    /// <summary>Names one extra document, applied after the per-feature files.</summary>
    public const string FileVariable = "ARGON_CONFIG_FILE";

    public const string DefaultDirectory = "conf.d";

    /// <summary>
    /// Inserts the feature configuration sources ahead of the environment variables, and reports what
    /// it found. Diagnostics are returned rather than thrown so the caller can print them alongside
    /// the validation findings.
    /// </summary>
    public static IReadOnlyList<ClusterDiagnostic> AddFeatureConfiguration(
        this WebApplicationBuilder builder,
        RoleDescriptor             role)
    {
        var found = Discover(role, builder.Environment.ContentRootPath);

        if (found.Sources.Count > 0)
            Insert(builder.Configuration, found.Sources);

        return found.Diagnostics;
    }

    /// <summary>
    /// Finds the files a role would load, without touching a host. Shared by the host and by
    /// <c>--validate-config</c>, so the command can never check something other than what boots.
    /// </summary>
    public static FeatureConfigurationFiles Discover(RoleDescriptor role, string contentRoot)
    {
        var diagnostics = new List<ClusterDiagnostic>();
        var sources     = new List<IConfigurationSource>();

        string? directory;
        try
        {
            directory = ResolveDirectory(contentRoot);
        }
        catch (DirectoryNotFoundException e)
        {
            diagnostics.Add(ClusterDiagnostic.Error("C4", e.Message, role.Id));
            directory = null;
        }

        if (directory is not null)
            sources.AddRange(FeatureFiles(directory, role, diagnostics));

        if (Environment.GetEnvironmentVariable(FileVariable) is { Length: > 0 } overridePath)
        {
            if (!File.Exists(overridePath))
                diagnostics.Add(ClusterDiagnostic.Error("C4",
                    $"{FileVariable} points at '{overridePath}', which does not exist", role.Id, overridePath));
            else
                sources.Add(JsonSource(overridePath));
        }

        return new FeatureConfigurationFiles
        {
            Sources     = sources,
            Diagnostics = diagnostics
        };
    }

    /// <summary>
    /// The directory scanned for per-feature files, or <c>null</c> when there is none. An explicitly
    /// configured directory that does not exist is a mistake worth reporting; the default one simply
    /// being absent is not.
    /// </summary>
    public static string? ResolveDirectory(string contentRoot)
    {
        if (Environment.GetEnvironmentVariable(DirectoryVariable) is not { Length: > 0 } configured)
        {
            var fallback = Path.Combine(contentRoot, DefaultDirectory);
            return Directory.Exists(fallback) ? fallback : null;
        }

        if (!Directory.Exists(configured))
            throw new DirectoryNotFoundException(
                $"{DirectoryVariable} points at '{configured}', which does not exist");

        return configured;
    }

    private static IEnumerable<IConfigurationSource> FeatureFiles(
        string                         directory,
        RoleDescriptor                 role,
        ICollection<ClusterDiagnostic> diagnostics)
    {
        var enabled = role.Features.Ordered.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);

            if (!enabled.TryGetValue(name, out var feature))
            {
                // Only a role that could have run the feature can tell a typo from a file meant for a
                // different role, so this stays a warning: one conf.d is expected to serve the whole
                // deployment, and every role will see files that are not its business.
                diagnostics.Add(ClusterDiagnostic.Warning("C7",
                    $"'{Path.GetFileName(path)}' does not name a feature this role enables; ignored", role.Id, name));
                continue;
            }

            if (OwnershipViolations(path, feature, diagnostics, role) is not { Count: 0 })
                continue;

            yield return JsonSource(path);
        }
    }

    /// <summary>
    /// Top-level keys in a feature file that the feature did not declare. Read through the
    /// configuration binder rather than a JSON parser so the comparison is against the same key shape
    /// the binder will see.
    /// </summary>
    private static List<string>? OwnershipViolations(
        string                         path,
        FeatureDefinition              feature,
        ICollection<ClusterDiagnostic> diagnostics,
        RoleDescriptor                 role)
    {
        var owned = feature.Options.Select(o => o.Section.Split(':')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> keys;
        try
        {
            // Absolute: ConfigurationBuilder resolves a relative path against its own base directory,
            // which is the binary's, not the one the file was enumerated from.
            keys = new ConfigurationBuilder()
               .AddJsonFile(Path.GetFullPath(path), optional: false, reloadOnChange: false)
               .Build()
               .GetChildren()
               .Select(child => child.Key)
               .ToList();
        }
        catch (Exception e)
        {
            diagnostics.Add(ClusterDiagnostic.Error("C4",
                $"'{Path.GetFileName(path)}' could not be read: {e.Message}", role.Id, path));
            return null;
        }

        var violations = keys.Where(key => !owned.Contains(key)).ToList();

        foreach (var section in violations)
            diagnostics.Add(ClusterDiagnostic.Error("C3",
                $"'{Path.GetFileName(path)}' sets section '{section}', which feature '{feature.Name}' does not " +
                $"own; it declares {Declared(feature)}", role.Id, section));

        return violations;
    }

    private static string Declared(FeatureDefinition feature)
        => feature.Options.Count == 0
            ? "no configuration at all"
            : string.Join(", ", feature.Options.Select(o => $"'{o.Section}'"));

    internal static JsonConfigurationSource JsonSource(string path)
    {
        var source = new JsonConfigurationSource
        {
            Path           = Path.GetFullPath(path),
            Optional       = true,
            ReloadOnChange = true
        };

        source.ResolveFileProvider();
        return source;
    }

    /// <summary>
    /// Puts the new sources immediately before the environment variables the host already added, so
    /// a file overrides <c>appsettings</c> while an environment variable still overrides the file.
    /// Appending would invert that and let a stale mount beat a deliberate override.
    /// </summary>
    private static void Insert(IConfigurationBuilder configuration, IReadOnlyList<IConfigurationSource> sources)
    {
        var at = configuration.Sources.Count;

        for (var i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is not EnvironmentVariablesConfigurationSource)
                continue;
            at = i;
            break;
        }

        for (var i = 0; i < sources.Count; i++)
            configuration.Sources.Insert(at + i, sources[i]);
    }
}
