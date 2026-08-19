namespace Argon.Features.Auth;

using Argon.Features.Clustering;

/// <summary>
/// How much each hardware signal is worth, and how much agreement makes two logins one machine.
/// </summary>
/// <remarks>
/// <para>Configuration rather than constants, and not for the usual reason. A weight table is a map
/// for anyone who wants to look like a different machine: it names which signals are worth spoofing
/// and how far each one gets them. In an open repository that is an evasion manual, so the numbers
/// live with the deployment and the source carries none of them.</para>
///
/// <para>Nothing here has a default, and an absent section is a supported deployment rather than a
/// misconfiguration: matching is simply off, every login is attributed to a machine never seen
/// before, and no fingerprint is stored. That is the direction that loses a signal instead of
/// inventing one — a half-filled table would match strangers to each other.</para>
///
/// <para>The codes are the wire format the client writes into the <c>hwv</c> field of the
/// <c>ArgonSecure</c> cookie. A signal with no weight here is not merely unscored, it is not parsed:
/// a component nobody can score is a component there is no reason to keep.</para>
/// </remarks>
public sealed class DeviceMatchingOptions : IValidatableFeatureOptions
{
    public const string SectionName = "auth:deviceMatching";

    /// <summary>Wire code to contribution.</summary>
    /// <remarks>
    /// A dictionary rather than a list so a deployment that means to change one signal writes one
    /// line. That also avoids the trap a list would set here: configuration merges dictionaries by
    /// key but appends to lists, so a deployment intending to replace a table would silently end up
    /// with both.
    /// </remarks>
    public Dictionary<string, int> Weights { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Score at or above which two fingerprints are treated as the same machine.</summary>
    public int SameMachineThreshold { get; set; }

    /// <summary>
    /// Values that arrive on the wire but identify nothing, and are dropped before scoring.
    /// </summary>
    /// <remarks>
    /// <para>OEM firmware very often ships a placeholder where a serial belongs. Treating one as data
    /// is worse than having no data at all: every unit from that vendor agrees on it, and agreement
    /// is what the score is made of.</para>
    ///
    /// <para>Configuration for a different reason than the weights above, and it keeps its defaults
    /// for that reason too. These are not secret — they are strings firmware prints on millions of
    /// machines, and knowing the list buys an evader nothing, since anyone wanting to go unrecognised
    /// sends random values rather than placeholders. What the list needs is to be <em>changeable</em>:
    /// a vendor ships a new placeholder, tens of thousands of machines start colliding on it, and the
    /// fix should be a settings change rather than a deploy.</para>
    ///
    /// <para>Configuration appends to this list rather than replacing it, which is the wanted
    /// behaviour for a deny list — a deployment adds what it has caught and keeps everything already
    /// known. The consequence is that an entry here cannot be removed by configuration, only by
    /// editing this list.</para>
    /// </remarks>
    public List<string> PlaceholderValues { get; set; } =
    [
        "", "0", "none", "null", "default string", "to be filled by o.e.m.", "to be filled by oem",
        "system serial number", "not applicable", "unknown", "00000000", "ffffffff",
        "00000000-0000-0000-0000-000000000000"
    ];

    /// <summary>Whether a usable table was configured at all.</summary>
    public bool IsConfigured => Weights.Count > 0 && SameMachineThreshold > 0;

    public void Validate(IFeatureConfigurationReport report)
    {
        // Said out loud even though it is legitimate, because the alternative reading of a silent
        // start — that matching is on and working — is the expensive one to discover later.
        if (!report.SectionExists)
        {
            report.Prefer(false, nameof(Weights),
                "were never configured, so device matching is off: every login is attributed to a " +
                "machine never seen before, and a hardware ban has nothing to match against");
            return;
        }

        report.Require(Weights.Count > 0, nameof(Weights),
            "is empty, so nothing the client reports would be parsed, let alone scored");

        foreach (var (code, weight) in Weights)
        {
            // The colon half only bites when these options are built in code: configuration reads a
            // colon as nesting, so a code written with one arrives as a signal with no weight and is
            // caught by the rule below instead.
            report.Require(!string.IsNullOrWhiteSpace(code) && !code.Contains(':') && !code.Contains(','),
                $"{nameof(Weights)}:{code}",
                "is not a usable code: a fingerprint is written as 'code:value' pairs separated by " +
                "commas, so a code containing either could never be read back");

            report.Require(weight > 0, $"{nameof(Weights)}:{code}",
                "is not positive; a signal that cannot add to a score is better removed than left " +
                "here, because it would still be parsed and stored against every device");
        }

        report.Prefer(PlaceholderValues.Count > 0, nameof(PlaceholderValues),
            "is empty, so a firmware placeholder counts as a serial and every unit from that vendor " +
            "agrees on it — which is how strangers become one machine");

        report.Require(SameMachineThreshold > 0, nameof(SameMachineThreshold),
            "is not positive, which would make every pair of fingerprints the same machine — " +
            "including two that agree on nothing at all");

        var total = Weights.Values.Where(weight => weight > 0).Sum();

        report.Require(SameMachineThreshold <= total, nameof(SameMachineThreshold),
            $"is above the {total} obtainable when every signal agrees, so no two logins could ever " +
            "match and each one would mint a machine of its own");

        // The property the numbers exist to hold. It used to be a comment next to the table; with
        // the table gone from the source there is nowhere left to state it except as a rule.
        //
        // Signals like a board model or a CPU model are shared by every unit ever sold of that
        // model. A table where those can reach the threshold between them is a table where two
        // strangers who bought the same laptop are one machine — and a hardware ban then lands on
        // whichever of them did nothing.
        var withoutTheStrongest = total - Weights.Values.OrderDescending().Take(2).Sum();

        report.Require(withoutTheStrongest < SameMachineThreshold, nameof(SameMachineThreshold),
            $"is at or below the {withoutTheStrongest} that every signal but the two strongest reach " +
            "together, so a match no longer needs a signal that identifies one physical machine");
    }
}
