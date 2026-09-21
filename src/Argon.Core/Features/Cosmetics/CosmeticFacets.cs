namespace Argon.Features.Cosmetics;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// One axis of a kind — a dimension the wearer composes rather than the operator authors.
/// </summary>
/// <remarks>
/// <para>This is what keeps the catalogue from having to enumerate combinations. A nickname style is
/// one kind with three axes; without them, every face crossed with every treatment crossed with
/// every colour would have had to be a row somebody authored and published.</para>
///
/// <para><b>An axis's options are the items of another kind.</b> Deliberately not a second concept:
/// a font with an uploaded file <i>is</i> a catalogue item, and a colour is one with a trivial
/// payload. Saying so means a new face or a new colour is added from the admin console like anything
/// else — with the same upload, the same publish gate, the same price and the same <c>IsEnabled</c>
/// switch — and none of it had to be built twice.</para>
/// </remarks>
public sealed record CosmeticFacetDefinition(string Id, string OptionKindKey);

/// <summary>
/// A wearer's choices on a kind's axes: axis id to the slug of the option item chosen.
/// </summary>
/// <remarks>
/// <para>Slugs rather than ids because the client matches a code-backed option to a file by slug,
/// and because a stored choice stays readable in the column when somebody is working out what went
/// wrong.</para>
///
/// <para>Read through <see cref="ICosmeticJsonCodec"/> like every other stored document, rather
/// than through one serializer's object model. A value that is not a string is the codec's error to
/// report, which is how the two of them are kept from disagreeing about what this column holds.</para>
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

    public static bool TryParse(
        string? json,
        out Dictionary<string, string> choices,
        [NotNullWhen(false)] out string? problem,
        ICosmeticJsonCodec? codec = null)
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

        if (!(codec ?? CosmeticJson.Default).TryRead<Dictionary<string, string>>(json, out var parsed, out problem))
            return false;

        choices = parsed;
        return true;
    }
}
