namespace Argon.Features.Moderation;

using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

public sealed class ContentModerator : IDisposable
{
    private readonly ModeratorConfig _config;
    private readonly InferenceSession _primary;
    private readonly InferenceSession? _secondary;
    private readonly string _primaryInputName;
    private readonly string? _secondaryInputName;

    public ContentModerator(ModeratorConfig config)
    {
        _config = config;

        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = config.InferenceThreads > 0
                ? config.InferenceThreads
                : Environment.ProcessorCount
        };

        _primary = new InferenceSession(config.PrimaryModel.ModelPath, opts);
        _primaryInputName = _primary.InputMetadata.First().Key;
        Warmup(_primary, _primaryInputName, config.PrimaryModel.InputSize);

        if (config.SecondaryModel is { ModelPath.Length: > 0 } secondary && File.Exists(secondary.ModelPath))
        {
            _secondary = new InferenceSession(secondary.ModelPath, opts);
            _secondaryInputName = _secondary.InputMetadata.First().Key;
            Warmup(_secondary, _secondaryInputName, secondary.InputSize);
        }
    }

    public ContentModerationResult Evaluate(Stream imageStream)
    {
        var sw = Stopwatch.StartNew();

        using var original = Image.Load<Rgb24>(imageStream);

        var s1 = InferFromImage(original, _primary, _primaryInputName, _config.PrimaryModel.InputSize);
        var s1Decision = ApplyRules(_config.PrimaryRules, s1);

        if (!s1Decision.Escalate || _secondary == null || _config.SecondaryRules == null)
        {
            sw.Stop();
            return new ContentModerationResult
            {
                Action = s1Decision.Action,
                StagesUsed = 1,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Scores = ToDict(s1)
            };
        }

        var s2 = InferFromImage(original, _secondary, _secondaryInputName!, _config.SecondaryModel!.InputSize);
        var s2Decision = ApplyRules(_config.SecondaryRules, s2);
        sw.Stop();

        return new ContentModerationResult
        {
            Action = s2Decision.Action,
            StagesUsed = 2,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            Scores = ToDict(s1),
            RefinedScores = ToDict(s2)
        };
    }

    internal static (ContentAction Action, bool Escalate) ApplyRules(PolicyRule[] rules, float[] scores)
    {
        foreach (var rule in rules)
        {
            bool matched;

            if (rule.InvertAsLowConfidence)
            {
                matched = scores.Max() < rule.Threshold;
            }
            else
            {
                var sum = Sum(scores, rule.ClassIndices);
                matched = sum >= rule.Threshold;

                if (matched && rule.SecondaryClassIndices is { Length: > 0 })
                {
                    var secondarySum = Sum(scores, rule.SecondaryClassIndices);
                    matched = secondarySum >= rule.SecondaryThreshold;
                }
            }

            if (matched)
                return (rule.Action, rule.Escalate);
        }

        return (ContentAction.Allow, false);
    }

    private static float Sum(float[] scores, int[] indices)
    {
        var total = 0f;
        foreach (var i in indices)
            if (i >= 0 && i < scores.Length)
                total += scores[i];
        return total;
    }

    private static float[] InferFromImage(Image<Rgb24> source, InferenceSession session, string inputName, int size)
    {
        using var resized = source.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(size, size),
            Mode = ResizeMode.Stretch
        }));

        var tensor = new DenseTensor<float>([1, size, size, 3]);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var px = resized[x, y];
            tensor[0, y, x, 0] = px.R / 255f;
            tensor[0, y, x, 1] = px.G / 255f;
            tensor[0, y, x, 2] = px.B / 255f;
        }

        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };
        using var results = session.Run(inputs);
        return results.First().AsEnumerable<float>().ToArray();
    }

    private Dictionary<string, float> ToDict(float[] scores) =>
        _config.ClassLabels
            .Select((label, i) => (label, score: i < scores.Length ? scores[i] : 0f))
            .ToDictionary(x => x.label, x => x.score);

    private static void Warmup(InferenceSession session, string inputName, int size)
    {
        var tensor = new DenseTensor<float>([1, size, size, 3]);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };
        using var results = session.Run(inputs);
        _ = results.First().AsEnumerable<float>().ToArray();
    }

    public void Dispose()
    {
        _primary.Dispose();
        _secondary?.Dispose();
    }
}
