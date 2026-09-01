namespace Argon.Features.Moderation;

using Argon.Features.Clustering;
using System.Text.Json.Serialization;

public enum ContentAction
{
    Allow,
    Deny
}

[GenerateSerializer, Immutable]
public sealed record ContentModerationResult
{
    [Id(0)] public required ContentAction Action { get; init; }
    [Id(1)] public required int StagesUsed { get; init; }
    [Id(2)] public required double ElapsedMs { get; init; }
    [Id(3)] public required Dictionary<string, float> Scores { get; init; }
    [Id(4)] public Dictionary<string, float>? RefinedScores { get; init; }
}

public class StageModelConfig
{
    public string ModelPath { get; set; } = string.Empty;
    public int InputSize { get; set; } = 224;
}

public class PolicyRule
{
    public int[] ClassIndices { get; set; } = [];
    public float Threshold { get; set; }
    public int[]? SecondaryClassIndices { get; set; }
    public float SecondaryThreshold { get; set; }
    public bool InvertAsLowConfidence { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContentAction Action { get; set; }

    public bool Escalate { get; set; }
}

public class ModeratorConfig : IValidatableFeatureOptions
{
    public const string SectionName = "Moderation";

    public string[] ClassLabels { get; set; } = [];
    public StageModelConfig PrimaryModel { get; set; } = new();
    public StageModelConfig? SecondaryModel { get; set; }
    public PolicyRule[] PrimaryRules { get; set; } = [];
    public PolicyRule[]? SecondaryRules { get; set; }
    public int InferenceThreads { get; set; }

    /// <summary>
    /// Whether this configuration describes a moderator that can actually run.
    /// </summary>
    /// <remarks>
    /// <para><b>The failure this exists for is silence.</b> When anything here is wrong — a model file
    /// that is not on the volume, a label list that does not line up with the rules written against
    /// it — the feature registers a no-op moderator, writes one warning, and the process starts
    /// perfectly. From then on every image is approved, which looks exactly like a service where
    /// nothing objectionable has been uploaded yet.</para>
    ///
    /// <para>An absent section stays legitimate and stays quiet: a deployment that does not want
    /// content moderation is entitled to leave it out. What is refused is a section that asks for
    /// moderation and describes one that cannot work.</para>
    /// </remarks>
    public void Validate(IFeatureConfigurationReport report)
    {
        if (!report.SectionExists)
            return;

        report.Require(ClassLabels.Length > 0, nameof(ClassLabels),
            "is empty, so every rule below refers to classes the model has no names for and the "
          + "moderator would silently fall back to approving everything");

        // The file check is the point of this rule. A model path is written once, at deploy time,
        // against a volume that is mounted separately -- so the two drift apart without anything
        // touching the configuration at all.
        report.RequireFile(PrimaryModel.ModelPath, $"{nameof(PrimaryModel)}:{nameof(StageModelConfig.ModelPath)}");
        report.RequireRange(PrimaryModel.InputSize, 16, 4096, $"{nameof(PrimaryModel)}:{nameof(StageModelConfig.InputSize)}");

        // Named but missing is a mistake; not named at all is a single-stage moderator, which is a
        // supported shape.
        if (!string.IsNullOrWhiteSpace(SecondaryModel?.ModelPath))
        {
            report.RequireFile(SecondaryModel.ModelPath, $"{nameof(SecondaryModel)}:{nameof(StageModelConfig.ModelPath)}");
            report.RequireRange(SecondaryModel.InputSize, 16, 4096, $"{nameof(SecondaryModel)}:{nameof(StageModelConfig.InputSize)}");
        }

        report.Require(PrimaryRules.Length > 0, nameof(PrimaryRules),
            "is empty, so the primary model is loaded, run on every upload, and its answer discarded");

        Check(report, PrimaryRules, nameof(PrimaryRules));
        Check(report, SecondaryRules ?? [], nameof(SecondaryRules));

        // Zero is "let the runtime decide", which is the usual answer; a negative one is not.
        report.RequireRange(InferenceThreads, 0, 256, nameof(InferenceThreads));
    }

    /// <summary>
    /// Rules that point at classes the model does not have, or at confidences that cannot occur.
    /// </summary>
    /// <remarks>
    /// An index past the end of the label list is the one worth catching: it does not throw, it
    /// simply never matches, so the rule it belongs to is dead and whatever it was meant to deny is
    /// allowed. Reordering the labels is all it takes.
    /// </remarks>
    private void Check(IFeatureConfigurationReport report, PolicyRule[] rules, string setting)
    {
        foreach (var (rule, position) in rules.Select((rule, index) => (rule, index)))
        {
            foreach (var index in rule.ClassIndices.Concat(rule.SecondaryClassIndices ?? []))
                report.Require(index >= 0 && index < ClassLabels.Length, setting,
                    $"rule {position} names class {index}, and there are {ClassLabels.Length} labels — "
                  + "the rule can never match, so what it was written to catch is allowed");

            foreach (var (threshold, name) in new[] { (rule.Threshold, "threshold"), (rule.SecondaryThreshold, "secondaryThreshold") })
                report.Require(threshold is >= 0f and <= 1f, setting,
                    $"rule {position} has a {name} of {threshold}, which is outside the range a "
                  + "confidence can take, so the rule either always fires or never does");
        }
    }
}

public class ContentViolationException(string message) : InvalidOperationException(message);
