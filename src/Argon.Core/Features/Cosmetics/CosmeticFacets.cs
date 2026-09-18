namespace Argon.Features.Cosmetics;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// One axis of a kind — a dimension the wearer composes rather than the operator authors.
/// </summary>
/// <remarks>
/// <para>This is what keeps the catalogue from having to enumerate combinations. A nickname style is
/// one row with three axes; without them, every face crossed with every treatment crossed with every
/// colour would have had to be a row somebody authored and published.</para>
///
/// <para><b>An axis's options are the items of another kind.</b> Deliberately not a second concept: a
/// font with an uploaded file <i>is</i> a catalogue item, and a colour is one with a trivial payload.
/// Saying so means a new face or a new colour is added from the admin console like anything else —
/// with the same upload, the same publish gate, the same price, the same <c>IsEnabled</c> switch and
/// the same ownership rules — and none of it had to be built twice.</para>
///
/// <para>What genuinely cannot come from a row is an option that <i>is</i> code, like a treatment
/// made of CSS. Those are items too, and the client carries a file per slug; an item whose slug this
/// build has no file for is simply not offered.</para>
/// </remarks>
public sealed record CosmeticFacetDefinition(string Id, string OptionKindKey);

/// <summary>
/// A wearer's choices on a kind's axes: axis id to the slug of the option item chosen.
/// </summary>
/// <remarks>
/// Slugs rather than ids because the client matches a code-backed option to a file by slug, and
/// because a stored choice stays readable in the column when somebody is working out what went
/// wrong.
/// </remarks>
public static class CosmeticChoices
{
    /// <summary>
    /// Generous for a flat map of short slugs, and small enough that the column cannot be used as
    /// storage. An axis holds one value, so nothing legitimate approaches it.
    /// </summary>
    public const int MaxLength = 1024;

    /// <summary>The value meaning nothing chosen, which every axis has to be able to express.</summary>
    public const string None = "none";

    public static bool TryParse(string? json, out Dictionary<string, string> choices, out string? problem)
    {
        choices = [];
        problem = null;

        if (string.IsNullOrWhiteSpace(json))
            return true;

        if (json.Length > MaxLength)
        {
            problem = $"longer than {MaxLength} characters";
            return false;
        }

        JObject parsed;

        try
        {
            parsed = JObject.Parse(json);
        }
        catch (JsonException error)
        {
            problem = error.Message;
            return false;
        }

        foreach (var (facetId, chosen) in parsed)
        {
            if (chosen?.Type is not JTokenType.String)
            {
                problem = $"axis '{facetId}' is not a string";
                return false;
            }

            choices[facetId] = chosen.Value<string>()!;
        }

        return true;
    }
}
