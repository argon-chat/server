namespace Argon.Features.Clustering;

/// <summary>
/// What a feature's validation rule writes its findings to.
/// </summary>
/// <remarks>
/// A rule is handed the bound options and this report, and is expected to say what is wrong rather
/// than to throw: every feature in the role is validated in one pass, so a deployment with three
/// broken settings learns about all three instead of discovering them one restart at a time.
/// <para>
/// Name the setting, not the class. <c>nameof(o.Port)</c> is enough — the reporter already knows the
/// feature and the section, and prints <c>account-console:port</c> for you.
/// </para>
/// </remarks>
public interface IFeatureConfigurationReport
{
    /// <summary>The configuration section the options were bound from, for messages that need it.</summary>
    string Section { get; }

    /// <summary>Whether the section carries any keys at all. A rule may want to stay quiet on an
    /// entirely absent section and let <see cref="Required"/> speak instead.</summary>
    bool SectionExists { get; }

    /// <summary>
    /// Binds another section, for a rule that genuinely depends on one — trust scoring only matters
    /// while the report system is enabled, and only the report system's section knows that.
    /// </summary>
    /// <remarks>
    /// A read, never a write, and always explicit. Reaching for it to avoid putting a setting where it
    /// belongs is how the ownership rule gets hollowed out.
    /// </remarks>
    TOther Read<TOther>(string section) where TOther : class;

    /// <summary>Records an error unless <paramref name="condition"/> holds.</summary>
    void Require(bool condition, string setting, string message);

    /// <summary>
    /// A finding the rule phrased itself, for a validator that already produces whole sentences and
    /// has no single setting to hang them on.
    /// </summary>
    void Invalid(string message);

    /// <summary>Records a warning unless <paramref name="condition"/> holds.</summary>
    void Prefer(bool condition, string setting, string message);

    /// <summary>The setting has no usable value.</summary>
    void Required(string? value, string setting);

    /// <summary>The setting must be an absolute URI with one of the given schemes.</summary>
    void RequireUri(string? value, string setting, params string[] schemes);

    /// <summary>The setting must name a file that exists.</summary>
    void RequireFile(string? path, string setting);

    void RequireRange(int value, int min, int max, string setting);

    void RequireRange(TimeSpan value, TimeSpan min, TimeSpan max, string setting);
}

/// <summary>
/// Collects one feature's findings and turns them into cluster diagnostics.
/// </summary>
internal sealed class FeatureConfigurationReport(
    string         feature,
    string         section,
    bool           sectionExists,
    ArgonRoleId?   role,
    IConfiguration configuration)
    : IFeatureConfigurationReport
{
    private readonly List<ClusterDiagnostic> diagnostics = [];

    public IReadOnlyList<ClusterDiagnostic> Diagnostics => diagnostics;

    public string Section       => section;
    public bool   SectionExists => sectionExists;

    public TOther Read<TOther>(string other) where TOther : class
    {
        var instance = Activator.CreateInstance<TOther>();
        configuration.GetSection(other).Bind(instance);
        return instance;
    }

    public void Require(bool condition, string setting, string message)
    {
        if (!condition)
            Error("C2", setting, message);
    }

    public void Invalid(string message)
        => diagnostics.Add(ClusterDiagnostic.Error("C2", $"{feature}: {message} (section '{section}')", role, section));

    public void Prefer(bool condition, string setting, string message)
    {
        if (!condition)
            diagnostics.Add(ClusterDiagnostic.Warning("C6", $"{feature}: {Key(setting)} — {message}", role, Key(setting)));
    }

    public void Required(string? value, string setting)
    {
        if (string.IsNullOrWhiteSpace(value))
            Error("C1", setting, "is required but has no value");
    }

    public void RequireUri(string? value, string setting, params string[] schemes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Error("C1", setting, "is required but has no value");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            Error("C2", setting, $"'{value}' is not an absolute URI");
            return;
        }

        if (schemes.Length > 0 && !schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            Error("C2", setting, $"scheme '{uri.Scheme}' is not one of {string.Join(", ", schemes)}");
    }

    public void RequireFile(string? path, string setting)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Error("C1", setting, "is required but has no value");
            return;
        }

        if (!File.Exists(path))
            Error("C2", setting, $"'{path}' does not exist");
    }

    public void RequireRange(int value, int min, int max, string setting)
    {
        if (value < min || value > max)
            Error("C2", setting, $"{value} is outside [{min}, {max}]");
    }

    public void RequireRange(TimeSpan value, TimeSpan min, TimeSpan max, string setting)
    {
        if (value < min || value > max)
            Error("C2", setting, $"{value} is outside [{min}, {max}]");
    }

    /// <summary>A member the type declares <c>required</c> that the section does not carry.</summary>
    internal void MissingRequired(string setting)
        => diagnostics.Add(ClusterDiagnostic.Error("C1",
            $"{feature}: {Key(setting)} is declared required and the section does not set it", role, Key(setting)));

    /// <summary>Findings from <c>[Range]</c>, <c>[Url]</c> and friends, folded into the same report.</summary>
    internal void Annotation(string? setting, string message)
        => diagnostics.Add(ClusterDiagnostic.Error("C3",
            setting is null ? $"{feature}: {message} (section '{section}')" : $"{feature}: {Key(setting)} {message}",
            role, setting is null ? section : Key(setting)));

    private void Error(string code, string setting, string message)
        => diagnostics.Add(ClusterDiagnostic.Error(code, $"{feature}: {Key(setting)} {message}", role, Key(setting)));

    private string Key(string setting)
        => $"{section}:{Camel(setting)}";

    /// <summary>
    /// Rules pass <c>nameof(o.Port)</c>, which is PascalCase; configuration keys are written
    /// camelCase everywhere in this repo, so the printed key matches what a person would edit.
    /// </summary>
    private static string Camel(string setting)
        => setting.Length > 0 && char.IsUpper(setting[0])
            ? char.ToLowerInvariant(setting[0]) + setting[1..]
            : setting;
}
