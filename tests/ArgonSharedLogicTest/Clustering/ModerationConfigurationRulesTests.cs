namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;

/// <summary>
/// Whether a deployment that asked for content moderation actually got one.
/// </summary>
/// <remarks>
/// <para>Every failure here is silent by construction. The feature reads its section, finds something
/// it cannot work with, registers a moderator that approves everything, logs one warning, and the
/// process starts and stays healthy. There is no request that fails, no probe that goes red, and no
/// metric that moves — and the product it produces, a service where nothing is ever flagged, is
/// indistinguishable from one where nothing objectionable has been uploaded.</para>
///
/// <para>So the checks live at configuration time, where a wrong answer stops the deploy instead of
/// quietly changing what the deploy does.</para>
/// </remarks>
[TestFixture]
public class ModerationConfigurationRulesTests
{
    /// <summary>
    /// Model files belonging to the running test and to nothing else.
    /// </summary>
    /// <remarks>
    /// <para>Per test rather than per fixture, because this assembly runs tests within a fixture
    /// concurrently and NUnit gives them one shared instance. Fields holding paths were therefore
    /// being rewritten and deleted underneath whichever test was mid-validation — which read as the
    /// model file not existing, which is exactly what the rule under test reports, so the fixture was
    /// accusing the code of its own bug.</para>
    ///
    /// <para>Cleaned up by the fixture rather than after each test, for the same reason: a teardown
    /// that deletes shared state is the thing that broke it.</para>
    /// </remarks>
    private static string Model(string role)
    {
        var path = Path.Combine(Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "argon-moderation-tests")).FullName,
            $"{role}-{TestContext.CurrentContext.Test.ID}.onnx");

        File.WriteAllBytes(path, [1]);

        return path;
    }

    [OneTimeTearDown]
    public void RemoveModelFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "argon-moderation-tests");

        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    /// <summary>
    /// Refused, and refused for the reason the test is about.
    /// </summary>
    /// <remarks>
    /// Not <c>Is.Not.Empty</c>. Every one of these starts from a configuration that is valid apart
    /// from one thing, so a bare "something was reported" passes just as well when the fixture itself
    /// is broken — which is how these tests read green while the model file they wrote was not on
    /// disk at all.
    /// </remarks>
    private static void Refused(IReadOnlyList<ClusterDiagnostic> diagnostics, string setting, string because)
    {
        Assert.That(diagnostics.Select(d => d.Message), Has.Some.Contains(setting), because);

        Assert.That(diagnostics.Where(d => !d.Message.Contains(setting)), Is.Empty,
            "the configuration was meant to be wrong in exactly one way, and something else was "
          + "reported too — so what this test proves is not what it says");
    }

    private IReadOnlyList<ClusterDiagnostic> Validate(params (string Key, string? Value)[] overrides)
    {
        var settings = new List<(string, string?)>
        {
            ("ModerationUnderTest:ClassLabels:0",          "Neutral"),
            ("ModerationUnderTest:ClassLabels:1",          "Porn"),
            ("ModerationUnderTest:PrimaryModel:ModelPath", Model("primary")),
            ("ModerationUnderTest:PrimaryModel:InputSize", "224"),
            ("ModerationUnderTest:PrimaryRules:0:ClassIndices:0", "1"),
            ("ModerationUnderTest:PrimaryRules:0:Threshold",      "0.8"),
            ("ModerationUnderTest:PrimaryRules:0:Action",         "Deny")
        };

        foreach (var (key, value) in overrides)
        {
            settings.RemoveAll(s => s.Item1 == key);
            settings.Add((key, value));
        }

        return FeatureConfigurationValidator
          .Validate(ConfigurationFixtures.Role<ModerationOptionsRole>(),
                    ConfigurationFixtures.From([.. settings]))
          .Diagnostics;
    }

    [Test]
    public void A_moderator_that_can_run_is_accepted()
        => Assert.That(Validate(), Is.Empty);

    /// <summary>
    /// The model named on a volume that does not have it.
    /// </summary>
    /// <remarks>
    /// The path and the volume are written in different files by different hands, so this is the way
    /// moderation actually breaks: nothing about the configuration changes, and the file stops being
    /// where it says.
    /// </remarks>
    [Test]
    public void A_primary_model_that_is_not_on_disk_is_refused()
        => Refused(Validate(("ModerationUnderTest:PrimaryModel:ModelPath", "/var/onnx/not-here.onnx")),
            "primaryModel:ModelPath",
            "a missing model started the process with moderation quietly switched off");

    /// <summary>
    /// A second stage named but not shipped.
    /// </summary>
    /// <remarks>
    /// Distinguished from not naming one at all, which is a single-stage moderator and a supported
    /// shape — the refusal is for asking for a stage and not providing it.
    /// </remarks>
    [Test]
    public void A_secondary_model_that_is_named_but_missing_is_refused()
        => Refused(Validate(("ModerationUnderTest:SecondaryModel:ModelPath", "/var/onnx/not-here.onnx")),
            "secondaryModel:ModelPath", "a second stage was asked for and not shipped");

    [Test]
    public void A_two_stage_moderator_is_accepted()
        => Assert.That(Validate(("ModerationUnderTest:SecondaryModel:ModelPath", Model("secondary"))), Is.Empty);

    /// <summary>
    /// A rule pointing at a class the model does not have.
    /// </summary>
    /// <remarks>
    /// This one does not throw and does not log: the rule simply never matches, so whatever it was
    /// written to deny is allowed. Reordering the label list is enough to cause it.
    /// </remarks>
    [Test]
    public void A_rule_naming_a_class_that_does_not_exist_is_refused()
        => Refused(Validate(("ModerationUnderTest:PrimaryRules:0:ClassIndices:0", "7")), "primaryRules",
            "a rule that can never fire was accepted, and what it was meant to catch is allowed");

    [Test]
    public void A_confidence_outside_zero_to_one_is_refused()
        => Refused(Validate(("ModerationUnderTest:PrimaryRules:0:Threshold", "1.5")), "primaryRules",
            "a rule that always fires or never does was accepted");

    /// <summary>
    /// Loading a model and then having no rule to judge its answer with.
    /// </summary>
    [Test]
    public void A_model_with_no_rules_is_refused()
        => Refused(Validate(("ModerationUnderTest:PrimaryRules:0:ClassIndices:0", null),
                                ("ModerationUnderTest:PrimaryRules:0:Threshold", null),
                                ("ModerationUnderTest:PrimaryRules:0:Action", null)),
            "primaryRules", "a model was loaded with nothing to judge its answer with");

    /// <summary>
    /// A section that is present and names no model is a deployment saying no.
    /// </summary>
    /// <remarks>
    /// The same switch the feature reads: an unnamed model is how <c>AddContentModeration</c> is told
    /// to register the no-op on purpose. Every role that merely has the feature — the co-hosted test
    /// host among them — is in this state, so a rule that refused it would stop them all from
    /// starting while catching nothing.
    /// </remarks>
    [Test]
    public void A_section_that_names_no_model_asks_for_nothing()
        => Assert.That(Validate(("ModerationUnderTest:PrimaryModel:ModelPath", ""),
                                ("ModerationUnderTest:ClassLabels:0", null),
                                ("ModerationUnderTest:ClassLabels:1", null),
                                ("ModerationUnderTest:PrimaryRules:0:ClassIndices:0", null),
                                ("ModerationUnderTest:PrimaryRules:0:Threshold", null),
                                ("ModerationUnderTest:PrimaryRules:0:Action", null)),
            Is.Empty);

    /// <summary>
    /// And a deployment that never asked for moderation is left alone.
    /// </summary>
    /// <remarks>
    /// The section being absent is a choice, not a mistake — a self-hosted instance is entitled to
    /// run without content moderation, and a rule that demanded models from it would stop every one
    /// of them from starting.
    /// </remarks>
    [Test]
    public void An_absent_section_asks_for_nothing()
        => Assert.That(
            FeatureConfigurationValidator
               .Validate(ConfigurationFixtures.Role<ModerationOptionsRole>(), ConfigurationFixtures.From())
               .Diagnostics,
            Is.Empty);
}
