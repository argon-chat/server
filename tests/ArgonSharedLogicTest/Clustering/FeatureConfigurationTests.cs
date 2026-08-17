namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;

/// <summary>
/// What a feature's own configuration rules do. The three levels — the <c>required</c> keyword, the
/// data annotations, and <see cref="IValidatableFeatureOptions"/> — all land in one report.
/// </summary>
[TestFixture]
public class FeatureConfigurationTests
{
    private static FeatureConfigurationReportSet Validate(params (string, string?)[] values)
        => FeatureConfigurationValidator.Validate(
            ConfigurationFixtures.Role<ConfiguredRole>(), ConfigurationFixtures.From(values));

    private static readonly (string, string?)[] Valid =
    [
        ("widget:endpoint", "https://example"),
        ("gadget:enabled", "false")
    ];

    [Test]
    public void A_section_that_satisfies_every_level_reports_nothing()
    {
        var report = Validate([..Valid, ("widget:fallback", "https://spare")]);

        Assert.That(report.Diagnostics, Is.Empty,
            string.Join(Environment.NewLine, report.Diagnostics.Select(d => d.ToString())));
        Assert.That(report.IsValid, Is.True);
    }

    /// <summary>
    /// The point of reading the keyword rather than adding an attribute: <c>required</c> was already
    /// on these classes and did nothing, because the binder never runs an object initializer.
    /// </summary>
    [Test]
    public void A_member_declared_required_must_be_present_in_configuration()
    {
        var report = Validate(("gadget:enabled", "false"));

        Assert.Multiple(() =>
        {
            Assert.That(report.Codes(), Does.Contain("C1"));
            Assert.That(report.Errors.Select(e => e.Target), Does.Contain("widget:endpoint"));
        });
    }

    /// <summary>
    /// Presence is read from configuration, not from the bound value, so a member explicitly set to
    /// its type's default still counts as set.
    /// </summary>
    [Test]
    public void A_required_member_set_to_an_empty_value_counts_as_present()
    {
        var report = Validate(("widget:endpoint", ""), ("gadget:enabled", "false"));

        Assert.That(report.Errors.Where(e => e.Code == "C1"), Is.Empty,
            "the key is in configuration; whether an empty endpoint is usable is the model's own rule to make");
    }

    [Test]
    public void A_data_annotation_failure_is_reported_against_the_setting()
    {
        var report = Validate([..Valid, ("widget:retries", "99")]);

        Assert.Multiple(() =>
        {
            Assert.That(report.Codes(), Does.Contain("C3"));
            Assert.That(report.Errors.Select(e => e.Target), Does.Contain("widget:retries"));
        });
    }

    [Test]
    public void The_models_own_rule_runs_and_names_the_setting()
    {
        var report = Validate([..Valid, ("widget:timeout", "-00:00:01")]);

        Assert.That(report.Errors.Select(e => e.Target), Does.Contain("widget:timeout"));
    }

    [Test]
    public void Prefer_produces_a_warning_rather_than_an_error()
    {
        var report = Validate(Valid);

        Assert.Multiple(() =>
        {
            Assert.That(report.Warnings.Select(w => w.Target), Does.Contain("widget:fallback"));
            Assert.That(report.IsValid, Is.True, "a preference must not stop a role from starting");
        });
    }

    /// <summary>
    /// Trust scoring only matters while the report system is on, and only the report system's section
    /// knows that. <c>Read</c> is how a rule reaches a section it does not own.
    /// </summary>
    [Test]
    public void A_rule_can_read_a_section_it_does_not_own()
    {
        var off = Validate(("widget:endpoint", "https://example"), ("gadget:enabled", "false"));
        var on  = Validate(("widget:endpoint", "https://example"), ("gadget:enabled", "true"));

        Assert.Multiple(() =>
        {
            Assert.That(off.Errors.Select(e => e.Target), Does.Not.Contain("sidecar:weight"));
            Assert.That(on.Errors.Select(e => e.Target), Does.Contain("sidecar:weight"));
        });
    }

    /// <summary>
    /// An absent section is not itself a finding — most features have workable defaults, and warning
    /// on each one would bury the findings that matter.
    /// </summary>
    [Test]
    public void A_section_left_entirely_at_its_defaults_is_not_reported()
    {
        var report = Validate([..Valid, ("widget:fallback", "https://spare")]);

        Assert.That(report.Diagnostics.Where(d => d.Target == "pair"), Is.Empty);
    }

    // ── declaration ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void The_section_defaults_to_the_feature_name_whichever_order_it_is_declared_in()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FeatureCatalog.Describe<WidgetFeature>().Options.Single().Section, Is.EqualTo("widget"));

            // GadgetFeature calls Options() before Named(), which is why the section is resolved when
            // the definition is built rather than when the call is made.
            Assert.That(FeatureCatalog.Describe<GadgetFeature>().Options.Single().Section, Is.EqualTo("gadget"));
        });
    }

    [Test]
    public void A_feature_may_declare_several_sections()
    {
        var sections = FeatureCatalog.Describe<PairFeature>().Options.Select(o => o.Section);

        Assert.That(sections, Is.EquivalentTo(new[] { "pair", "pair:sidecar" }));
    }

    [Test]
    public void Declaring_the_same_section_twice_is_rejected()
    {
        Assert.That(() => FeatureCatalog.Describe<DuplicateSectionFeature>(),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("same configuration section"));
    }

    [Test]
    public void Asking_a_feature_for_options_it_never_declared_says_what_it_does_declare()
    {
        var widget = FeatureCatalog.Describe<WidgetFeature>();

        Assert.That(() => widget.BindOptions<GadgetOptions>(ConfigurationFixtures.From()),
            Throws.InstanceOf<InvalidOperationException>()
                  .With.Message.Contains(nameof(WidgetOptions)));
    }

    [Test]
    public void Binding_reads_the_declared_section()
    {
        var options = FeatureCatalog.Describe<WidgetFeature>()
           .BindOptions<WidgetOptions>(ConfigurationFixtures.From(("widget:retries", "9")));

        Assert.That(options.Retries, Is.EqualTo(9));
    }
}
