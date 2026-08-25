namespace Argon.Features.Clustering;

using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;

/// <summary>
/// One options class a feature declares and the configuration section it comes from.
/// </summary>
/// <remarks>
/// Built inside the generic <c>Options&lt;TOptions&gt;</c> overload so everything past that point can
/// stay non-generic — the descriptor, the definition and the validator all deal in
/// <see cref="FeatureOptionsBinding"/> without knowing the options type.
/// <para>
/// Validation is the options type's own business: the <c>required</c> keyword, data annotations, and
/// <see cref="IValidatableFeatureOptions"/>. Nothing about a setting is declared anywhere but on the
/// class that holds it.
/// </para>
/// </remarks>
public sealed class FeatureOptionsBinding
{
    /// <summary>Only <see cref="Create{TOptions}"/> knows how to fill in the closures below.</summary>
    internal FeatureOptionsBinding()
    {
    }

    public required Type OptionsType { get; init; }

    /// <summary>Configuration path, colon-separated. This is the key a person edits.</summary>
    public required string Section { get; init; }

    /// <summary>Binds the section and appends this binding's findings to the report.</summary>
    internal Action<IConfiguration, FeatureConfigurationReport> Validate { get; init; } = null!;

    /// <summary>
    /// Registers the options with DI: bound to the section, validated by the same rules the
    /// diagnostic pass uses, and checked before the host accepts any traffic.
    /// </summary>
    internal Action<IServiceCollection, IConfiguration> Register { get; init; } = null!;

    /// <summary>
    /// Binds a throwaway instance of <see cref="OptionsType"/> from <see cref="Section"/>. Untyped
    /// because the caller usually has the binding, not the type — a feature reading its own settings
    /// goes through <c>ctx.Options&lt;T&gt;()</c> instead.
    /// </summary>
    public Func<IConfiguration, object> Bind { get; init; } = null!;

    public override string ToString()
        => $"{OptionsType.Name} <- {Section}";

    internal static FeatureOptionsBinding Create<TOptions>(string featureName, string section)
        where TOptions : class
        => new()
        {
            OptionsType = typeof(TOptions),
            Section     = section,
            Bind        = configuration => BindOptions<TOptions>(configuration, section),
            Validate    = (configuration, report) => EvaluateInto(configuration, section, typeof(TOptions), report),
            Register = (services, configuration) =>
            {
                var builder = services.AddOptions<TOptions>().Bind(configuration.GetSection(section));

                // The same rules run twice on purpose. The diagnostic pass reports every feature's
                // findings together and names them; this one is the backstop for a code path that
                // built the container without going through the pass at all.
                builder.Validate(_ => !HasErrors(configuration, section, featureName, typeof(TOptions)),
                    $"configuration section '{section}' is invalid; run --validate-config for the detail");

                builder.ValidateOnStart();
            }
        };

    private static TOptions BindOptions<TOptions>(IConfiguration configuration, string section)
        where TOptions : class
    {
        var options = Activator.CreateInstance<TOptions>();
        configuration.GetSection(section).Bind(options);
        return options;
    }

    private static bool HasErrors(IConfiguration configuration, string section, string featureName, Type optionsType)
    {
        var report = new FeatureConfigurationReport(featureName, section,
            configuration.GetSection(section).Exists(), role: null, configuration);

        EvaluateInto(configuration, section, optionsType, report);

        return report.Diagnostics.Any(d => d.Severity is ClusterDiagnosticSeverity.Error);
    }

    /// <summary>
    /// Runs all three levels against one section: presence of the <c>required</c> members, then the
    /// data annotations, then the model's own rule.
    /// </summary>
    private static void EvaluateInto(
        IConfiguration            configuration,
        string                    section,
        Type                      optionsType,
        FeatureConfigurationReport report)
    {
        var options = Activator.CreateInstance(optionsType)!;
        configuration.GetSection(section).Bind(options);

        MissingRequiredMembers(configuration.GetSection(section), optionsType, report);

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        foreach (var failure in results)
            report.Annotation(failure.MemberNames.FirstOrDefault(), failure.ErrorMessage ?? "is invalid");

        if (options is IValidatableFeatureOptions validatable)
            validatable.Validate(report);
    }

    /// <summary>
    /// Reports every member the type declares <c>required</c> that the section does not carry.
    /// </summary>
    /// <remarks>
    /// Presence is tested against configuration rather than against the bound value, because a bound
    /// value cannot tell "absent" from "explicitly set to the default" — and for a numeric or a
    /// <see cref="TimeSpan"/> those are different things.
    /// <para>
    /// The <c>required</c> keyword was already the intent on these classes; it just did nothing,
    /// because the binder constructs them the way <c>IOptionsFactory</c> does and never runs an object
    /// initializer. This makes the keyword mean what it reads as.
    /// </para>
    /// </remarks>
    private static void MissingRequiredMembers(IConfigurationSection section, Type optionsType, FeatureConfigurationReport report)
    {
        foreach (var member in RequiredMembers(optionsType))
            if (!section.GetSection(member).Exists())
                report.MissingRequired(member);
    }

    private static readonly ConcurrentDictionary<Type, string[]> requiredMembers = new();

    private static string[] RequiredMembers(Type optionsType)
        => requiredMembers.GetOrAdd(optionsType, static type =>
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            return type.GetProperties(flags).Cast<MemberInfo>()
               .Concat(type.GetFields(flags))
               .Where(m => m.GetCustomAttribute<RequiredMemberAttribute>() is not null)
               .Select(m => m.Name)
               .ToArray();
        });
}
