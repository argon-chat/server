namespace Argon.Features.Cosmetics;

/// <summary>
/// What a kind lets its wearer fill in, and how wide it may sit on the profile board.
/// </summary>
/// <param name="ContentType">
/// The type the wearer's content deserializes into, validated exactly like a catalogue payload.
/// Null for a kind with nothing to author.
/// </param>
/// <param name="MinWidth">Narrowest it may be made, in grid columns — what its editor needs to fit.</param>
/// <param name="MaxWidth">Widest the wearer may make it.</param>
/// <param name="MinHeight">Shortest it may be made, in grid rows.</param>
/// <param name="MaxHeight">Tallest it may be made.</param>
public sealed record CosmeticBoardDefinition(
    Type? ContentType, int MinWidth, int MaxWidth, int MinHeight, int MaxHeight);

/// <summary>
/// Holds a wearer's authored content to the schema its kind declares.
/// </summary>
/// <remarks>
/// <para>A separate gate from <see cref="CosmeticChoices"/>, because the two are different problems.
/// A choice is a slug from a closed list and is checked by looking it up. Content is written by a
/// person — a heading, a handful of tags — so what protects everybody else who later renders that
/// profile is the schema: a declared type, its data annotations, and a hard cap on length before any
/// of that is attempted.</para>
///
/// <para>The cap comes first deliberately. Deserializing a megabyte to discover it was a megabyte is
/// work somebody can ask for as often as they like.</para>
/// </remarks>
public static class CosmeticContent
{
    /// <summary>
    /// Room for a heading and a short list, and nothing like enough to use a profile as storage.
    /// </summary>
    public const int MaxLength = 4096;

    public static CosmeticPayloadValidation Validate(CosmeticKindDefinition kind, string? contentJson)
    {
        var schema = kind.AuthoredType;

        if (string.IsNullOrWhiteSpace(contentJson))
        {
            // Nothing written is always acceptable: a widget just added has no content yet, and a
            // kind that declares no schema must never be handed any.
            return CosmeticPayloadValidation.Valid;
        }

        if (schema is null)
            return CosmeticPayloadValidation.Invalid($"kind '{kind.Key}' has nothing for a wearer to fill in");

        if (contentJson.Length > MaxLength)
            return CosmeticPayloadValidation.Invalid($"content is longer than {MaxLength} characters");

        return CosmeticPayloadValidator.Validate(schema, contentJson);
    }

    /// <summary>One card's place on the grid, held to what its kind and the board allow.</summary>
    public static (int X, int Y, int W, int H) ClampCell(CosmeticKindDefinition kind, int x, int y, int w, int h)
    {
        var board = kind.Board;
        var width = Math.Clamp(w, Math.Max(1, board?.MinWidth ?? 1), Math.Min(Columns, Math.Max(1, board?.MaxWidth ?? 1)));

        var height = Math.Clamp(h, Math.Max(1, board?.MinHeight ?? 1), Math.Max(1, board?.MaxHeight ?? 1));

        return (
            Math.Clamp(x, 0, Math.Max(0, Columns - width)),
            // The card's own height counts against the board, not just its top edge: a card dropped
            // at the last row would otherwise hang off the bottom by its whole length, and a profile
            // is as tall as the lowest thing on it.
            Math.Clamp(y, 0, Math.Max(0, Rows - height)),
            width,
            height);
    }

    /// <summary>
    /// How wide the board is.
    /// </summary>
    /// <remarks>
    /// <para>Four rather than two, because two columns can only ever be symmetrical: a card is left
    /// or right and nothing sits in the middle. Four lets a half-width card be centred and a board be
    /// deliberately lopsided, which is what people actually arrange things to be.</para>
    ///
    /// <para>Fixed rather than a setting: a card's width is stored in columns, so changing how many
    /// there are would resize every card anybody ever placed.</para>
    /// </remarks>
    public const int Columns = 4;

    /// <summary>
    /// How tall the board may get.
    /// </summary>
    /// <remarks>
    /// A profile is as tall as the lowest card on it, so an unbounded board is an unbounded profile —
    /// somebody dragging a card a long way down stretched the card of everybody looking at them.
    /// Sixteen rows is three or four cards deep and still fits on a screen.
    /// </remarks>
    public const int Rows = 16;
}
