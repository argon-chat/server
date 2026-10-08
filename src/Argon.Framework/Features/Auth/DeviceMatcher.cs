namespace Argon.Features.Auth;

using System.Collections.Frozen;
using Microsoft.Extensions.Options;

/// <summary>
/// Reads hardware fingerprints off the wire and decides whether two of them are one machine.
/// </summary>
/// <remarks>
/// <para>A service rather than static members on <see cref="DeviceFingerprint"/>, because the weights
/// are configuration now — <see cref="DeviceMatchingOptions"/> says why they cannot be in the
/// source. Parsing moved with them: the codes worth reading are exactly the codes with a weight, and
/// after the move there is nowhere else that knows the set.</para>
///
/// <para>Unconfigured, this reads nothing and matches nothing. Callers get an empty fingerprint and
/// a false verdict rather than an exception, which is the same answer they already had to handle for
/// a client too old to report hardware at all.</para>
/// </remarks>
public sealed class DeviceMatcher
{
    private readonly FrozenDictionary<string, int> weights;
    private readonly FrozenSet<string>             placeholders;
    private readonly int                           threshold;

    public DeviceMatcher(IOptions<DeviceMatchingOptions> options)
    {
        var configured = options.Value;

        weights      = configured.Weights.Where(signal => signal.Value > 0).ToFrozenDictionary(StringComparer.Ordinal);
        threshold    = configured.SameMachineThreshold;
        placeholders = configured.PlaceholderValues.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether a weight table was configured. False means no device is ever recognised.</summary>
    public bool Enabled => weights.Count > 0 && threshold > 0;

    /// <summary>Total weight obtainable when every signal is present on both sides.</summary>
    public int MaxScore => weights.Values.Sum();

    /// <summary>Score at or above which two fingerprints are one machine.</summary>
    /// <remarks>
    /// Exposed for the caller that has to rank several candidates and would otherwise score each of
    /// them twice — once to compare and once to pick the best.
    /// </remarks>
    public int SameMachineThreshold => threshold;

    /// <summary>
    /// Reads the <c>hwv</c> field of the <c>ArgonSecure</c> cookie: <c>1;mg:abc,su:def</c>.
    /// </summary>
    /// <remarks>
    /// Unparseable input yields <see cref="DeviceFingerprint.Empty"/> rather than throwing. This runs
    /// on the auth path for every caller, including old clients that send no such field at all, and a
    /// malformed fingerprint is a reason to learn nothing about the device — never a reason to refuse
    /// a login.
    /// </remarks>
    public DeviceFingerprint Parse(string? raw)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(raw))
            return DeviceFingerprint.Empty;

        var separator = raw.IndexOf(';');

        if (separator <= 0 ||
            !int.TryParse(raw.AsSpan(0, separator), out var version) ||
            version != DeviceFingerprint.CurrentVersion)
            return DeviceFingerprint.Empty;

        var components = new Dictionary<string, string>();

        foreach (var pair in raw.AsSpan(separator + 1).ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = pair.IndexOf(':');

            if (colon <= 0)
                continue;

            var code  = pair[..colon].Trim();
            var value = pair[(colon + 1)..].Trim();

            // Unknown codes are skipped rather than kept: a component with no weight cannot affect a
            // score, and storing it would only invite it to be weighted later by accident.
            if (!weights.ContainsKey(code) || placeholders.Contains(value))
                continue;

            components[code] = value;
        }

        return components.Count == 0 ? DeviceFingerprint.Empty : new DeviceFingerprint { Components = components };
    }

    /// <summary>Writes a fingerprint back in the form <see cref="Parse"/> reads.</summary>
    public string Serialize(DeviceFingerprint fingerprint)
        => $"{DeviceFingerprint.CurrentVersion};" +
           string.Join(",", fingerprint.Components.OrderBy(component => component.Key)
              .Select(component => $"{component.Key}:{component.Value}"));

    /// <summary>
    /// How strongly two fingerprints look like the same machine.
    /// </summary>
    /// <remarks>
    /// Only components present on <em>both</em> sides can score. A signal one side could not read is
    /// missing information, and missing information is not evidence either way — counting an absence
    /// as agreement would make two machines that both fail to report a motherboard serial look
    /// alike, which is the collision this whole mechanism exists to avoid.
    /// </remarks>
    public int Score(DeviceFingerprint one, DeviceFingerprint other)
    {
        var score = 0;

        foreach (var (code, value) in one.Components)
        {
            if (other.Components.TryGetValue(code, out var theirs) &&
                string.Equals(value, theirs, StringComparison.Ordinal) &&
                weights.TryGetValue(code, out var weight))
                score += weight;
        }

        return score;
    }

    public bool IsSameMachine(DeviceFingerprint one, DeviceFingerprint other)
        => Enabled && Score(one, other) >= threshold;
}
