namespace Argon.Features.Moderation;

using ArgonContracts;

/// <summary>
/// What is known about one reporter at the moment they filed, for deciding whether they are a
/// different person from the others on the case.
/// </summary>
/// <param name="AddressHash">HMAC of the address, or null when the deployment keeps none.</param>
/// <param name="DeviceHash">HMAC of the device id, or null when the deployment keeps none.</param>
public readonly record struct ReporterSignal(
    Guid           ReporterId,
    string?        AddressHash,
    string?        DeviceHash,
    int            AccountAgeDays,
    int            Credibility,
    DateTimeOffset FiledAt);

public readonly record struct EscalationDecision(bool IsEscalated, string? Rule)
{
    public static EscalationDecision None => default;
}

/// <summary>Names stored on a case that say why it was marked urgent.</summary>
public static class EscalationRules
{
    public const string CriticalCategory       = "CRITICAL_CATEGORY";
    public const string IndependentCluster     = "INDEPENDENT_CLUSTER";
    public const string HighCredibilitySerious = "HIGH_CRED_SERIOUS";
    public const string LowTrustTarget         = "LOW_TRUST_TARGET";
}

/// <summary>
/// The part of the report system that is arithmetic: how a case is ranked, when it is urgent, and
/// how many distinct people are actually behind it.
/// </summary>
/// <remarks>
/// <para>Pure on purpose. Every decision here is a function of the options and of a few numbers
/// the grain has already read, so the whole policy can be exercised in a unit test with no
/// database — which is where the properties that matter (that a farm of accounts on one machine
/// is one reporter, that nothing here does anything but rank) are pinned.</para>
///
/// <para>Nothing in this class changes a target. A decision that comes out "escalated" puts the
/// case at the top of a queue a person reads; that is the whole effect.</para>
/// </remarks>
public static class ReportPolicy
{
    /// <summary>Whether this reporter's report can count towards independence at all.</summary>
    public static bool Qualifies(ReportEscalationOptions options, in ReporterSignal signal)
        => signal.AccountAgeDays >= options.IndependentReporterMinAccountAgeDays
        && signal.Credibility    >= options.IndependentReporterMinCredibility;

    /// <summary>
    /// The reporters that count as different people, earliest first.
    /// </summary>
    /// <remarks>
    /// <para>Greedy in filing order: a reporter is counted unless someone already counted shares
    /// their address or their device, or they are the same account again, or they do not qualify.
    /// Sharing is judged only on hashes the deployment actually stored — a null hash is "not
    /// known", never "the same as another unknown".</para>
    ///
    /// <para>This is conservative in the direction that matters. Two flatmates on one connection
    /// are one reporter here, and the price of that is a case reaching the threshold one report
    /// later; the price of the opposite mistake was three accounts taking down any message.</para>
    /// </remarks>
    public static IReadOnlyList<Guid> Independent(ReportEscalationOptions options, IEnumerable<ReporterSignal> signals, DateTimeOffset now)
    {
        var since     = now.AddMinutes(-options.WindowMinutes);
        var counted   = new List<Guid>();
        var seen      = new HashSet<Guid>();
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var devices   = new HashSet<string>(StringComparer.Ordinal);

        foreach (var signal in signals.Where(s => s.FiledAt > since).OrderBy(s => s.FiledAt))
        {
            if (!seen.Add(signal.ReporterId))
                continue;

            if (!Qualifies(options, signal))
                continue;

            if (signal.AddressHash is { } address && addresses.Contains(address))
                continue;

            if (signal.DeviceHash is { } device && devices.Contains(device))
                continue;

            counted.Add(signal.ReporterId);

            if (signal.AddressHash is { } a)
                addresses.Add(a);
            if (signal.DeviceHash is { } d)
                devices.Add(d);
        }

        return counted;
    }

    public static int CountIndependent(ReportEscalationOptions options, IEnumerable<ReporterSignal> signals, DateTimeOffset now)
        => Independent(options, signals, now).Count;

    /// <summary>Where the case sits in the queue. Higher is sooner.</summary>
    public static int ComputePriority(ReportPriorityOptions options, ReportCategory category, int bestCredibility, int independentReporters)
        => options.CategoryBase.GetValueOrDefault(category, options.DefaultBase)
         + Math.Max(0, bestCredibility) * options.CredibilityMultiplier
         + Math.Min(Math.Max(0, independentReporters) * options.IndependentReporterBoost, options.IndependentReporterBoostCap);

    /// <summary>The weightier of two categories, by the deployment's table.</summary>
    public static ReportCategory Higher(ReportPriorityOptions options, ReportCategory a, ReportCategory b)
        => options.CategoryBase.GetValueOrDefault(b, options.DefaultBase) > options.CategoryBase.GetValueOrDefault(a, options.DefaultBase) ? b : a;

    /// <summary>
    /// Whether the case is urgent, and by which rule.
    /// </summary>
    /// <param name="independentReporters">From <see cref="Independent"/>, the new report included.</param>
    /// <param name="reporterCredibility">Of the reporter filing now.</param>
    /// <param name="targetTrustScore">Of the person reported, or null when the target is not a person or has never been scored.</param>
    public static EscalationDecision Evaluate(
        ReportEscalationOptions options,
        ReportCategory          category,
        int                     independentReporters,
        int                     reporterCredibility,
        int?                    targetTrustScore)
    {
        if (options.CriticalCategories.Contains(category))
            return new EscalationDecision(true, EscalationRules.CriticalCategory);

        if (independentReporters >= options.IndependentReportersThreshold)
            return new EscalationDecision(true, EscalationRules.IndependentCluster);

        var serious = options.SeriousCategories.Contains(category);

        if (serious && reporterCredibility >= options.HighCredibilityThreshold)
            return new EscalationDecision(true, EscalationRules.HighCredibilitySerious);

        if (serious && targetTrustScore is { } trust && trust < options.LowTrustTargetThreshold)
            return new EscalationDecision(true, EscalationRules.LowTrustTarget);

        return EscalationDecision.None;
    }
}
