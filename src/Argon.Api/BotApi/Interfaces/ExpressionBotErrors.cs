namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

/// <summary>A refused sticker, emoji or upload request, shared by IExpressions and IFiles.</summary>
public static class ExpressionBotErrors
{
    public static readonly BotError InsufficientRights = new(403, "insufficient_permissions",
        "Bot lacks CreateExpressions in this space, or the pack or item is not one it made. Bots never delete or reorder.");

    public static readonly BotError NotFound = new(404, "not_found", "The pack, item or file does not exist.");

    public static readonly BotError InvalidFormat = new(400, "invalid_format",
        "A title, slug, name, emoji, keyword or external id is not acceptable, or the file is not (format, dimensions, duration, frame rate).");

    public static readonly BotError TooLarge = BotErrors.TooLarge;

    public static readonly BotError QuotaExceeded = new(422, "quota_exceeded",
        "The space's packs or slots, the pack, or the bot's unclaimed uploads are full.");

    public static readonly BotError ContentRejected = new(422, "content_rejected", "Moderation refused the file.");

    public static readonly BotError NameTaken = new(409, "name_taken", "The slug or the emoji name is already used in this space.");

    public static readonly BotError RenderUnavailable = new(503, "render_unavailable",
        "The server cannot render this animated format at the moment. Retry later.");

    public static readonly BotError SpaceRateLimited = new(429, "space_rate_limited",
        "The bots' budget for sticker and emoji changes in this space is spent for the minute; see Retry-After.");

    /// <summary>The budget is a window of a minute from its first use, so a minute is the most there is to wait.</summary>
    public const int RetryAfterSeconds = 60;

    public static BotError For(ExpressionError error) => error switch
    {
        ExpressionError.FORBIDDEN        => InsufficientRights,
        ExpressionError.NOT_FOUND        => NotFound,
        ExpressionError.INVALID_FORMAT   => InvalidFormat,
        ExpressionError.TOO_LARGE        => TooLarge,
        ExpressionError.QUOTA_EXCEEDED   => QuotaExceeded,
        ExpressionError.CONTENT_REJECTED => ContentRejected,
        ExpressionError.NAME_TAKEN       => NameTaken,
        ExpressionError.RATE_LIMITED     => SpaceRateLimited,
        _                                => throw new InvalidOperationException($"unhandled ExpressionError {error}")
    };

    public static BotApiException Raise(HttpContext ctx, ExpressionError error)
    {
        if (error == ExpressionError.RATE_LIMITED)
            ctx.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();

        return For(error).Raise();
    }
}
