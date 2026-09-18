namespace Argon.Features.Cosmetics;

/// <summary>
/// Whether a kind is switched on, stated once so the console and the read path cannot disagree.
/// </summary>
/// <remarks>
/// <para><b>A kind is on unless a flag row exists and says otherwise.</b> The feature-flag grain
/// evaluates a flag nobody created as <i>disabled</i>, which is right for an experiment and wrong
/// here: a kind exists because a file in the build declares it, and requiring somebody to also
/// create a database row before shipped code does anything is the failure mode where a feature is
/// live, correct, and invisible — with nothing in any log to say why.</para>
///
/// <para>So the flag is a kill switch rather than an enabler. <c>SetCosmeticKindEnabled</c> creates
/// the row the first time a kind is turned off, and from then on the row is the answer.</para>
/// </remarks>
public static class CosmeticKindGate
{
    public static bool IsEnabled(bool flagExists, bool flagSaysEnabled)
        => !flagExists || flagSaysEnabled;
}
