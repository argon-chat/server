namespace Argon.Features.Expressions;

public interface IExpressionFileValidator
{
    ValueTask<ExpressionValidation> ValidateAsync(Stream file, ExpressionKind kind, ExpressionFormat format, CancellationToken ct);

    /// <summary>A first-frame thumbnail: WEBP, exactly the item's dimensions.</summary>
    ValueTask<ExpressionValidation> ValidateThumbAsync(Stream webp, int width, int height, CancellationToken ct);
}

/// <param name="FileSize">Size of what gets stored: <paramref name="Reencoded"/> when set, else the upload.</param>
/// <param name="ContentType">Type of the upload as sniffed.</param>
/// <param name="Reencoded">Bytes to store instead of the upload (PNG as lossless WEBP, plain Lottie JSON as TGS).</param>
public sealed record ExpressionValidation(
    bool             Ok,
    ExpressionError  Error,
    ExpressionFormat Format,
    int              Width,
    int              Height,
    int              FileSize,
    string           ContentType,
    byte[]?          Reencoded,
    string?          ReencodedContentType,
    byte[]?          Outline,
    double           DurationSeconds,
    double           Fps)
{
    public string StoredContentType => ReencodedContentType ?? ContentType;

    public static ExpressionValidation Failed(ExpressionError error, ExpressionFormat format)
        => new(false, error, format, 0, 0, 0, string.Empty, null, null, null, 0, 0);
}

public static class ExpressionContentTypes
{
    public const string Png  = "image/png";
    public const string Webp = "image/webp";
    public const string Json = "application/json";
    public const string Tgs  = "application/x-tgsticker";
    public const string Webm = "video/webm";
}
