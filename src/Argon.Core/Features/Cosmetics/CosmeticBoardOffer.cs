namespace Argon.Features.Cosmetics;

using Argon.Core.Entities.Data;

/// <summary>
/// What an operator has decided about one board card, held to what its kind can actually draw.
/// </summary>
/// <param name="MaxPerBoard">How many of this card one board may hold. One makes it unique.</param>
/// <param name="DefaultWidth">The size a card of this row is created at.</param>
/// <param name="MaxWidth">The kind's own ceiling, passed on so a picker can hold a choice to it.</param>
public readonly record struct CosmeticBoardOffer(
    int MaxPerBoard,
    int DefaultWidth,
    int DefaultHeight,
    int MinWidth,
    int MaxWidth,
    int MinHeight,
    int MaxHeight)
{
    /// <summary>
    /// The row's decisions, each held inside the kind's capability.
    /// </summary>
    /// <remarks>
    /// Resolved in one place and read everywhere — the grain enforcing it, the catalogue telling a
    /// client, the console showing an operator. A second copy of this arithmetic is how a picker ends
    /// up offering something the write path then refuses.
    /// </remarks>
    public static CosmeticBoardOffer? Resolve(CosmeticKindDefinition kind, CosmeticItemEntity item)
    {
        var board = kind.Board;

        if (board is null)
            return null;

        var maxWidth = Math.Min(CosmeticContent.Columns, board.MaxWidth);
        var width    = Math.Clamp(item.BoardDefaultW ?? board.MinWidth, board.MinWidth, maxWidth);
        var height   = Math.Clamp(item.BoardDefaultH ?? board.MinHeight, board.MinHeight, board.MaxHeight);

        return new CosmeticBoardOffer(
            Math.Clamp(item.MaxPerBoard ?? kind.MaxSlots, 1, kind.MaxSlots),
            width,
            height,
            board.MinWidth,
            maxWidth,
            board.MinHeight,
            board.MaxHeight);
    }
}
