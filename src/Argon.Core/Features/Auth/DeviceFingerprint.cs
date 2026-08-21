namespace Argon.Features.Auth;

using System.Collections.Frozen;

/// <summary>
/// One hardware signal, and how much it is worth when deciding whether two logins are the same machine.
/// </summary>
/// <param name="Code">Short wire code, e.g. <c>mg</c>. Stable — it is part of the cookie format.</param>
/// <param name="Weight">
/// Contribution to the match score. Chosen by how well the signal identifies a single physical unit,
/// not by how easy it was to read.
/// </param>
public readonly record struct DeviceSignal(string Code, int Weight);

/// <summary>
/// The hardware signals a client reported, as a set of independent components rather than one hash.
/// </summary>
/// <remarks>
/// <para>The client used to fold everything into a single device id and send that. Two problems with
/// it, and this type exists for both. One: a single value is all-or-nothing, so replacing a disk
/// makes a returning user look like a stranger and changing one signal makes a returning stranger
/// look new. Two: the value the client computed <em>was</em> the identity — the server had no way to
/// weigh it, only to compare it.</para>
///
/// <para>Components arrive already hashed by the client, so no raw serial number is ever on the wire
/// or in a log. That costs nothing here: matching is per-component equality, never similarity inside
/// one component, so a hash compares exactly as well as the original.</para>
/// </remarks>
public sealed record DeviceFingerprint
{
    /// <summary>The wire format version this parser understands.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Every signal, by how well it pins down one physical machine.
    /// </summary>
    /// <remarks>
    /// <para><c>mg</c> (Windows MachineGuid) and <c>su</c> (SMBIOS UUID) carry the weight because
    /// they are per-installation and per-unit respectively, and both survive the hardware changes
    /// that people actually make.</para>
    ///
    /// <para><c>cpu</c> is deliberately near-worthless. <c>Win32_Processor.ProcessorId</c> is not a
    /// serial number — it is the CPUID signature and feature bits, identical on every unit of the
    /// same model, because per-unit serials were dropped after the Pentium III. It was carrying real
    /// weight in the old single-hash scheme, which is how two strangers with the same laptop could
    /// end up with the same device id. Kept only because it still separates an Intel from an AMD.</para>
    /// </remarks>
    public static readonly FrozenSet<DeviceSignal> Signals = new HashSet<DeviceSignal>
    {
        new("mg",  40), // Windows MachineGuid — survives hardware swaps, dies on OS reinstall
        new("su",  30), // SMBIOS system UUID — survives OS reinstall, dies with the board
        new("ds",  15), // system drive serial
        new("mac", 10), // physical adapters, sorted
        new("mb",   8), // motherboard serial — frequently blank on OEM hardware
        new("vs",   5), // volume serial — dies on a format
        new("cpu",  2), // CPUID: model, not unit. See remarks.
    }.ToFrozenSet();

    private static readonly FrozenDictionary<string, int> WeightOf =
        Signals.ToFrozenDictionary(x => x.Code, x => x.Weight);

    /// <summary>Total weight obtainable when every signal is present on both sides.</summary>
    public static int MaxScore { get; } = Signals.Sum(x => x.Weight);

    /// <summary>
    /// Score at or above which two fingerprints are treated as the same machine.
    /// </summary>
    /// <remarks>
    /// Set so that the weak signals cannot reach it together: <c>mb</c> and <c>cpu</c> sum to ten,
    /// and those are exactly the two the old scheme leaned on. Reaching this threshold requires at
    /// least one of the two strong per-machine signals to agree.
    /// </remarks>
    public const int SameMachineThreshold = 60;

    /// <summary>Present components, by code. Absent and junk values are not stored at all.</summary>
    public required IReadOnlyDictionary<string, string> Components { get; init; }

    public static DeviceFingerprint Empty { get; } = new() { Components = new Dictionary<string, string>() };

    public bool IsEmpty => Components.Count == 0;

    /// <summary>
    /// Values that are present on the wire but identify nothing.
    /// </summary>
    /// <remarks>
    /// OEM firmware very often ships a placeholder rather than a serial. Treating those as data is
    /// worse than having no data: every machine from that vendor would agree on it, and agreement is
    /// what the score is made of. They are dropped at parse time so they can never contribute.
    /// </remarks>
    private static readonly FrozenSet<string> Junk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "", "0", "none", "null", "default string", "to be filled by o.e.m.", "to be filled by oem",
        "system serial number", "not applicable", "unknown", "00000000", "ffffffff",
        "00000000-0000-0000-0000-000000000000"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the <c>hwv</c> field of the <c>ArgonSecure</c> cookie: <c>1;mg:abc,su:def</c>.
    /// </summary>
    /// <remarks>
    /// Unparseable input yields <see cref="Empty"/> rather than throwing. This runs on the auth path
    /// for every caller, including old clients that send no such field at all, and a malformed
    /// fingerprint is a reason to learn nothing about the device — never a reason to refuse a login.
    /// </remarks>
    public static DeviceFingerprint Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Empty;

        var separator = raw.IndexOf(';');

        if (separator <= 0 ||
            !int.TryParse(raw.AsSpan(0, separator), out var version) ||
            version != CurrentVersion)
            return Empty;

        var components = new Dictionary<string, string>();

        foreach (var pair in raw.AsSpan(separator + 1).ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = pair.IndexOf(':');

            if (colon <= 0)
                continue;

            var code  = pair[..colon].Trim();
            var value = pair[(colon + 1)..].Trim();

            // Unknown codes are skipped rather than kept: a component with no weight cannot affect
            // a score, and storing it would only invite it to be weighted later by accident.
            if (!WeightOf.ContainsKey(code) || Junk.Contains(value))
                continue;

            components[code] = value;
        }

        return components.Count == 0 ? Empty : new DeviceFingerprint { Components = components };
    }

    /// <summary>
    /// How strongly this fingerprint and <paramref name="other"/> look like the same machine.
    /// </summary>
    /// <remarks>
    /// Only components present on <em>both</em> sides can score. A signal one side could not read is
    /// missing information, and missing information is not evidence either way — counting an absence
    /// as agreement would make two machines that both fail to report a motherboard serial look
    /// alike, which is the collision this whole type exists to avoid.
    /// </remarks>
    public int ScoreAgainst(DeviceFingerprint other)
    {
        var score = 0;

        foreach (var (code, value) in Components)
        {
            if (other.Components.TryGetValue(code, out var theirs) &&
                string.Equals(value, theirs, StringComparison.Ordinal))
                score += WeightOf[code];
        }

        return score;
    }

    public bool IsSameMachineAs(DeviceFingerprint other)
        => ScoreAgainst(other) >= SameMachineThreshold;
}
