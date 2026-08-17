namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Where a feature's settings may come from, and which source wins when more than one has an answer.
/// </summary>
/// <remarks>
/// Precedence, lowest first: <c>appsettings</c>, <c>conf.d/&lt;feature&gt;.json</c>,
/// <c>$ARGON_CONFIG_FILE</c>, environment variables. The image carries the defaults and the mounted
/// file carries the deployment's intent, so a file beats <c>appsettings</c>; an environment variable
/// still beats the file, which is what makes a one-off override possible without editing a mount.
/// </remarks>
[TestFixture, NonParallelizable]
public class FeatureConfigurationSourcesTests
{
    private string          directory = null!;
    private RoleDescriptor  role      = null!;
    private List<string>    variables = [];

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"argon-conf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        role      = ConfigurationFixtures.Role<ConfiguredRole>();
        variables = [];

        Set(FeatureConfigurationSources.DirectoryVariable, directory);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var variable in variables)
            Environment.SetEnvironmentVariable(variable, null);

        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private void Set(string name, string? value)
    {
        variables.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private void Write(string name, string json)
        => File.WriteAllText(Path.Combine(directory, $"{name}.json"), json);

    private string WriteOutside(string json)
    {
        var path = Path.Combine(directory, "..", $"argon-override-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        variables.Add("__cleanup__");
        return path;
    }

    private (IConfiguration Configuration, IReadOnlyList<ClusterDiagnostic> Diagnostics) Build(
        params (string Key, string? Value)[] appsettings)
    {
        var found   = FeatureConfigurationSources.Discover(role, directory);
        var builder = new ConfigurationBuilder()
           .AddInMemoryCollection(appsettings.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));

        foreach (var source in found.Sources)
            builder.Add(source);

        builder.AddEnvironmentVariables();

        return (builder.Build(), found.Diagnostics);
    }

    // ── loading ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void A_per_feature_file_is_loaded()
    {
        Write("widget", """{ "widget": { "retries": 9 } }""");

        var (configuration, diagnostics) = Build();

        Assert.Multiple(() =>
        {
            Assert.That(configuration["widget:retries"], Is.EqualTo("9"));
            Assert.That(diagnostics, Is.Empty);
        });
    }

    /// <summary>
    /// The file's content is section-shaped, so a block can be cut out of <c>appsettings.json</c> and
    /// pasted into <c>conf.d</c> unchanged.
    /// </summary>
    [Test]
    public void A_per_feature_file_overrides_appsettings()
    {
        Write("widget", """{ "widget": { "retries": 9 } }""");

        var (configuration, _) = Build(("widget:retries", "1"));

        Assert.That(configuration["widget:retries"], Is.EqualTo("9"));
    }

    [Test]
    public void An_environment_variable_overrides_a_per_feature_file()
    {
        Write("widget", """{ "widget": { "retries": 9 } }""");
        Set("widget__retries", "4");

        var (configuration, _) = Build(("widget:retries", "1"));

        Assert.That(configuration["widget:retries"], Is.EqualTo("4"),
            "a deliberate override must beat a mount that may be stale");
    }

    [Test]
    public void The_override_file_is_applied_after_the_per_feature_files()
    {
        Write("widget", """{ "widget": { "retries": 9 } }""");
        Set(FeatureConfigurationSources.FileVariable, WriteOutside("""{ "widget": { "retries": 5 } }"""));

        var (configuration, diagnostics) = Build();

        Assert.Multiple(() =>
        {
            Assert.That(configuration["widget:retries"], Is.EqualTo("5"));
            Assert.That(diagnostics, Is.Empty);
        });
    }

    // ── ownership ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The property that makes per-feature files safe: a file may only set what its own feature
    /// declared, so nothing can quietly reconfigure a neighbour.
    /// </summary>
    [Test]
    public void A_feature_file_that_sets_another_features_section_is_rejected_whole()
    {
        Write("widget", """{ "widget": { "retries": 9 }, "gadget": { "size": 1 } }""");

        var (configuration, diagnostics) = Build();

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.Select(d => d.Code), Does.Contain("C3"));
            Assert.That(configuration["gadget:size"], Is.Null, "the offending file must not be applied");
            Assert.That(configuration["widget:retries"], Is.Null,
                "and not half-applied either — the file is refused, not filtered");
        });
    }

    /// <summary>
    /// The override file is deliberately not ownership-checked. It is one document for the whole
    /// process, which is the point of having it as well as <c>conf.d</c>.
    /// </summary>
    [Test]
    public void The_override_file_may_set_any_section()
    {
        Set(FeatureConfigurationSources.FileVariable, WriteOutside("""{ "gadget": { "size": 1 } }"""));

        var (configuration, diagnostics) = Build();

        Assert.Multiple(() =>
        {
            Assert.That(configuration["gadget:size"], Is.EqualTo("1"));
            Assert.That(diagnostics, Is.Empty);
        });
    }

    // ── files that name nothing ─────────────────────────────────────────────────────────────

    [Test]
    public void A_file_naming_no_enabled_feature_is_a_warning_not_an_error()
    {
        Write("nonesuch", """{ "nonesuch": { "x": 1 } }""");

        var (_, diagnostics) = Build();

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.Select(d => d.Code), Does.Contain("C7"));
            Assert.That(diagnostics.Where(d => d.Severity is ClusterDiagnosticSeverity.Error), Is.Empty,
                "one conf.d serves every role, so each role sees files that are not its business");
        });
    }

    [Test]
    public void A_missing_override_file_is_an_error()
    {
        Set(FeatureConfigurationSources.FileVariable, Path.Combine(directory, "absent.json"));

        var (_, diagnostics) = Build();

        Assert.That(diagnostics.Select(d => d.Code), Does.Contain("C4"));
    }

    [Test]
    public void A_configured_directory_that_does_not_exist_is_an_error()
    {
        Set(FeatureConfigurationSources.DirectoryVariable, Path.Combine(directory, "absent"));

        var found = FeatureConfigurationSources.Discover(role, directory);

        Assert.That(found.Diagnostics.Select(d => d.Code), Does.Contain("C4"));
    }

    [Test]
    public void An_unparseable_file_is_reported_rather_than_thrown()
    {
        Write("widget", "{ not json");

        var (_, diagnostics) = Build();

        Assert.That(diagnostics.Where(d => d.Severity is ClusterDiagnosticSeverity.Error), Is.Not.Empty);
    }
}
