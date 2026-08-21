namespace Argon.Features.Moderation;

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

public class ModeratorConfig
{
    public const string SectionName = "Moderation";

    public string[] ClassLabels { get; set; } = [];
    public StageModelConfig PrimaryModel { get; set; } = new();
    public StageModelConfig? SecondaryModel { get; set; }
    public PolicyRule[] PrimaryRules { get; set; } = [];
    public PolicyRule[]? SecondaryRules { get; set; }
    public int InferenceThreads { get; set; }
}

public class ContentViolationException(string message) : InvalidOperationException(message);
