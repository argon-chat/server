namespace Argon.Features.Clustering;

/// <summary>
/// An options class that knows what a usable value looks like.
/// </summary>
/// <remarks>
/// The rule lives on the model rather than on the feature that declares it, so everything about a
/// setting — its type, its default, its documentation and what makes it wrong — is in one file. A
/// person adding a property sees the rule they have to extend; a person reading the rule sees the
/// property it is about.
/// <para>
/// Three levels, cheapest first, and they compose:
/// <list type="number">
/// <item>the <c>required</c> keyword — the setting has to be present in configuration at all;</item>
/// <item>data annotations (<c>[Range]</c>, <c>[Url]</c>, <c>[MaxLength]</c>) — shape;</item>
/// <item>this — anything that needs a condition, a warning, or more than one property at once.</item>
/// </list>
/// Reach for this one only when the first two cannot say it.
/// </para>
/// </remarks>
public interface IValidatableFeatureOptions
{
    void Validate(IFeatureConfigurationReport report);
}
