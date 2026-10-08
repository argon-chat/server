namespace Argon.Features.Clustering;

/// <summary>
/// Runs every enabled feature's configuration rule against the configuration a role would actually
/// start with, and collects the findings.
/// </summary>
/// <remarks>
/// Rule catalogue — the codes are the identifiers, the same way E1..E9 are for
/// <see cref="ClusterValidator"/>:
/// <list type="table">
/// <item><term>C1</term><description>a setting the feature declared required has no value.</description></item>
/// <item><term>C2</term><description>a setting has a value the feature rejects — out of range,
/// unparseable, a file that is not there.</description></item>
/// <item><term>C3</term><description>either a data annotation on the options class failed, or a
/// <c>conf.d</c> file set a section its feature does not own.</description></item>
/// <item><term>C4</term><description><c>ARGON_CONFIG_FILE</c> names a file that does not
/// exist.</description></item>
/// <item><term>C6</term><description>warning: a value the feature accepts but would rather you
/// changed.</description></item>
/// <item><term>C7</term><description>warning: a file in <c>conf.d</c> names no feature this role
/// enables. Expected when one directory serves every role; a typo otherwise.</description></item>
/// </list>
/// <para>
/// This runs at boot for the role being started, and on demand for any role through
/// <c>--validate-config</c>. Both go through here so the two can never disagree.
/// </para>
/// </remarks>
public static class FeatureConfigurationValidator
{
    public static FeatureConfigurationReportSet Validate(RoleDescriptor role, IConfiguration configuration)
    {
        var diagnostics = new List<ClusterDiagnostic>();

        foreach (var feature in role.Features.Ordered)
        foreach (var binding in feature.Options)
        {
            var section = configuration.GetSection(binding.Section);
            var exists  = section.Exists() && section.GetChildren().Any();

            // An absent section is not itself a finding. Whether the defaults will do is the options
            // type's answer to give: a required member missing is C1, and anything subtler is what
            // IValidatableFeatureOptions is for. Warning on every section a role leaves at its
            // defaults would bury the two findings that matter under ten that do not.
            var report = new FeatureConfigurationReport(feature.Name, binding.Section, exists, role.Id, configuration);

            binding.Validate(configuration, report);

            diagnostics.AddRange(report.Diagnostics);
        }

        return new FeatureConfigurationReportSet
        {
            Role        = role.Id,
            Diagnostics = diagnostics
        };
    }
}

public sealed class FeatureConfigurationReportSet
{
    public required ArgonRoleId                     Role        { get; init; }
    public required IReadOnlyList<ClusterDiagnostic> Diagnostics { get; init; }

    public IEnumerable<ClusterDiagnostic> Errors
        => Diagnostics.Where(d => d.Severity is ClusterDiagnosticSeverity.Error);

    public IEnumerable<ClusterDiagnostic> Warnings
        => Diagnostics.Where(d => d.Severity is ClusterDiagnosticSeverity.Warning);

    public bool IsValid => !Errors.Any();
}
