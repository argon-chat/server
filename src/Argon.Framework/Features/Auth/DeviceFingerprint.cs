namespace Argon.Features.Auth;

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
///
/// <para>Deliberately inert. Which signals are read, what each is worth and how much agreement makes
/// two logins one machine all live in <see cref="DeviceMatchingOptions"/>, and the reading and
/// scoring live in <see cref="DeviceMatcher"/> — see the options for why none of it is here.</para>
/// </remarks>
public sealed record DeviceFingerprint
{
    /// <summary>The wire format version the parser understands.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Present components, by code. Absent and junk values are not stored at all.</summary>
    public required IReadOnlyDictionary<string, string> Components { get; init; }

    public static DeviceFingerprint Empty { get; } = new() { Components = new Dictionary<string, string>() };

    public bool IsEmpty => Components.Count == 0;
}
