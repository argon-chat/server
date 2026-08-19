namespace ArgonSharedLogicTest;

using Argon.Api.Clustering;
using Argon.Features.Clustering;
using Microsoft.Extensions.Configuration;

/// <summary>
/// The properties a weight table has to hold, now that no weight table is in the source.
/// </summary>
/// <remarks>
/// <para>The numbers moved to configuration because published they are an evasion manual: they say
/// which signals are worth spoofing and how far each gets you. What moved with them is the reasoning
/// that used to sit in comments beside the table — that the weak signals must not reach the
/// threshold between them, that the threshold must be reachable at all — and that reasoning had
/// nowhere to live except here, as rules a deployment is checked against before it starts.</para>
///
/// <para>This is the half of the old fingerprint fixture that could not move into
/// <see cref="DeviceMatcherTests"/>: those tests demonstrate the properties on a table of their own,
/// which proves nothing about the table a deployment actually wrote.</para>
///
/// <para>The tables here are invented, like that fixture's and for the same reason. What is being
/// tested is a rule, and a rule is exercised as well by six made-up signals as by the real ones.</para>
/// </remarks>
[TestFixture]
public class DeviceMatchingRulesTests
{
    private const string Section = "auth:deviceMatching";

    private static RoleDescriptor EntryPoint()
        => ArgonClusterCatalog.Build(new ClusterScanScope
        {
            Assemblies = [typeof(EntryPointRole).Assembly, typeof(IArgonRole).Assembly]
        }).Require(ArgonRoleId.EntryPoint);

    /// <summary>
    /// Findings about this section only, over the shipped <c>appsettings.json</c>.
    /// </summary>
    /// <remarks>
    /// Narrowed to the section on purpose: the role brings Redis, JWT and the rest, all of which
    /// have rules of their own, and asserting that the whole report is clean would be a test of
    /// those instead of this one.
    /// </remarks>
    private static (string[] Errors, string[] Warnings) Validate(params (string Key, string? Value)[] values)
    {
        var report = FeatureConfigurationValidator.Validate(EntryPoint(),
            new ConfigurationBuilder()
               .AddJsonFile(Path.Combine(TestContext.CurrentContext.TestDirectory, "appsettings.json"), optional: false)
               .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
               .Build());

        return (Mine(report.Errors), Mine(report.Warnings));
    }

    private static string[] Mine(IEnumerable<ClusterDiagnostic> diagnostics)
        => diagnostics
           .Where(d => d.Target?.StartsWith(Section, StringComparison.Ordinal) is true)
           .Select(d => d.ToString())
           .ToArray();

    private static (string Key, string? Value)[] Table(int threshold, params (string Code, int Weight)[] weights)
        =>
        [
            ($"{Section}:SameMachineThreshold", threshold.ToString()),
            .. weights.Select(w => ($"{Section}:Weights:{w.Code}", (string?)w.Weight.ToString()))
        ];

    /// <summary>
    /// A self-hosted instance has little ban evasion to speak of and a real interest in not keeping a
    /// hardware inventory, so an unwritten section is a supported deployment rather than a mistake.
    /// </summary>
    [Test]
    public void An_unconfigured_table_is_said_out_loud_but_not_refused()
    {
        var (errors, warnings) = Validate();

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            Assert.That(warnings, Has.Length.EqualTo(1), "silence would read as matching being on");
        });
    }

    [Test]
    public void An_ordinary_table_passes()
    {
        var (errors, warnings) = Validate(Table(150,
            ("alpha", 100), ("beta", 100), ("gamma", 25), ("delta", 20), ("epsilon", 10), ("zeta", 5)));

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors));
            Assert.That(warnings, Is.Empty);
        });
    }

    /// <summary>
    /// The rule the whole mechanism exists for: a board model and a CPU model are shared by every
    /// unit ever sold of that model, so a table where the weak signals reach the threshold between
    /// them makes two strangers who bought the same laptop into one machine — and a hardware ban
    /// then lands on whichever of them did nothing.
    /// </summary>
    [Test]
    public void A_threshold_the_weak_signals_can_reach_between_them_is_refused()
    {
        // Everything except the two strongest sums to exactly the threshold, so a match no longer
        // needs a signal that identifies one physical machine.
        var (errors, _) = Validate(Table(60,
            ("alpha", 100), ("beta", 100), ("gamma", 25), ("delta", 20), ("epsilon", 10), ("zeta", 5)));

        Assert.That(errors, Has.Length.EqualTo(1), string.Join(Environment.NewLine, errors));
        Assert.That(errors[0], Does.Contain("sameMachineThreshold"));
    }

    /// <summary>
    /// The opposite failure, and the quieter one: nothing ever matches, every login mints a machine
    /// of its own, and the observation table grows without ever recognising anybody.
    /// </summary>
    [Test]
    public void A_threshold_no_two_logins_could_reach_is_refused()
    {
        var (errors, _) = Validate(Table(900, ("alpha", 100), ("beta", 100), ("gamma", 25)));

        Assert.That(errors, Has.Length.EqualTo(1), string.Join(Environment.NewLine, errors));
        Assert.That(errors[0], Does.Contain("sameMachineThreshold"));
    }

    /// <summary>
    /// A fingerprint is written as comma-separated <c>code:value</c> pairs, so a code carrying a
    /// comma would be stored and never read back — a signal that silently stops counting.
    /// </summary>
    /// <remarks>
    /// The colon half of the same rule is not tested here because it cannot be reached from a
    /// settings file: configuration reads a colon as nesting, so a code written with one arrives as a
    /// signal with no value and is refused for having no weight instead. The rule still
    /// earns its place — these options are also built directly, by tests and by anything that
    /// composes them in code — but only this half is one a deployment can write.
    /// </remarks>
    [Test]
    public void A_code_that_could_not_be_read_back_is_refused()
    {
        var (errors, _) = Validate(Table(150, ("alpha", 100), ("beta", 100), ("gam,ma", 25)));

        Assert.That(errors.Any(e => e.Contains("gam,ma")), Is.True, string.Join(Environment.NewLine, errors));
    }

    [Test]
    public void A_signal_that_cannot_add_to_a_score_is_refused_rather_than_carried()
    {
        // Zero-weighted it would still be parsed and written against every device on record, which
        // is the cost of a signal without any of the benefit.
        var (errors, _) = Validate(Table(150, ("alpha", 100), ("beta", 100), ("epsilon", 0)));

        Assert.That(errors.Any(e => e.Contains("epsilon")), Is.True, string.Join(Environment.NewLine, errors));
    }
}
