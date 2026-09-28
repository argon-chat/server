namespace Argon.Features.Expressions;

using System.Buffers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

public sealed class ExpressionFileValidator : IExpressionFileValidator
{
    private static readonly DecoderOptions Decoding = new()
    {
        Configuration = new Configuration(new PngConfigurationModule(), new WebpConfigurationModule()),
        MaxFrames     = 2
    };

    private static readonly WebpEncoder LosslessWebp = new() { FileFormat = WebpFileFormatType.Lossless };

    public async ValueTask<ExpressionValidation> ValidateAsync(Stream file, ExpressionKind kind, ExpressionFormat format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        var cap = ExpressionLimits.MaxBytes(kind, format);
        if (cap == 0)
            return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, format);

        // A plain-JSON Lottie may be larger than the cap; it is judged by its gzipped size.
        var data = await ReadBoundedAsync(file, format == ExpressionFormat.Lottie ? ExpressionLimits.LottieMaxJsonBytes : cap, ct);
        if (data is null)
            return ExpressionValidation.Failed(ExpressionError.TOO_LARGE, format);

        return format switch
        {
            ExpressionFormat.Static => await ValidateStaticAsync(data, kind, cap, ct),
            ExpressionFormat.Lottie => ValidateLottie(data, kind, cap),
            ExpressionFormat.Video  => ValidateVideo(data, kind, cap),
            _                       => ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, format)
        };
    }

    public async ValueTask<ExpressionValidation> ValidateThumbAsync(Stream webp, int width, int height, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(webp);

        var data = await ReadBoundedAsync(webp, ExpressionLimits.ThumbMaxBytes, ct);
        if (data is null)
            return ExpressionValidation.Failed(ExpressionError.TOO_LARGE, ExpressionFormat.Static);
        if (!IsWebp(data))
            return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, ExpressionFormat.Static);

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var       info   = await Image.IdentifyAsync(Decoding, stream, ct);
            if (info.Width != width || info.Height != height)
                return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, ExpressionFormat.Static);

            stream.Position = 0;
            using var image = await Image.LoadAsync<Rgba32>(Decoding, stream, ct);
            if (image.Frames.Count != 1)
                return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, ExpressionFormat.Static);

            return new ExpressionValidation(true, ExpressionError.NONE, ExpressionFormat.Static, width, height, data.Length,
                ExpressionContentTypes.Webp, null, null, null, 0, 0);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, ExpressionFormat.Static);
        }
    }

    private static async ValueTask<ExpressionValidation> ValidateStaticAsync(byte[] data, ExpressionKind kind, int cap, CancellationToken ct)
    {
        var png = IsPng(data);
        if (!png && !IsWebp(data))
            return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, ExpressionFormat.Static);

        try
        {
            using var stream = new MemoryStream(data, writable: false);

            // Dimensions come from the header before anything is decoded, which also stops pixel bombs.
            var info = await Image.IdentifyAsync(Decoding, stream, ct);
            if (!ExpressionLimits.DimensionsFit(kind, ExpressionFormat.Static, info.Width, info.Height))
                return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, ExpressionFormat.Static);

            stream.Position = 0;
            using var image = await Image.LoadAsync<Rgba32>(Decoding, stream, ct);
            if (image.Frames.Count != 1)
                return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, ExpressionFormat.Static);

            byte[]? reencoded = null;

            if (png)
            {
                image.Metadata.ExifProfile = null;
                image.Metadata.XmpProfile  = null;

                using var output = new MemoryStream();
                await image.SaveAsWebpAsync(output, LosslessWebp, ct);
                reencoded = output.ToArray();

                if (reencoded.Length > cap)
                    return ExpressionValidation.Failed(ExpressionError.TOO_LARGE, ExpressionFormat.Static);
            }

            var outline = kind == ExpressionKind.Sticker ? OutlineTracer.FromAlpha(image) : null;

            return new ExpressionValidation(true, ExpressionError.NONE, ExpressionFormat.Static, image.Width, image.Height,
                reencoded?.Length ?? data.Length, png ? ExpressionContentTypes.Png : ExpressionContentTypes.Webp,
                reencoded, reencoded is null ? null : ExpressionContentTypes.Webp, outline, 0, 0);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, ExpressionFormat.Static);
        }
    }

    private static ExpressionValidation ValidateLottie(byte[] data, ExpressionKind kind, int cap)
    {
        byte[]  json;
        byte[]? reencoded = null;
        string  contentType;

        if (LottieInspector.IsGzip(data))
        {
            if (data.Length > cap)
                return ExpressionValidation.Failed(ExpressionError.TOO_LARGE, ExpressionFormat.Lottie);

            var inflated = LottieInspector.Gunzip(data, ExpressionLimits.LottieMaxJsonBytes, out var tooLarge);
            if (inflated is null)
                return ExpressionValidation.Failed(tooLarge ? ExpressionError.TOO_LARGE : ExpressionError.INVALID_FORMAT, ExpressionFormat.Lottie);

            json        = inflated;
            contentType = ExpressionContentTypes.Tgs;
        }
        else if (LottieInspector.LooksLikeJson(data))
        {
            json        = data;
            contentType = ExpressionContentTypes.Json;
        }
        else
        {
            return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, ExpressionFormat.Lottie);
        }

        var side = kind == ExpressionKind.Emoji ? ExpressionLimits.EmojiSide : ExpressionLimits.StickerSide;

        if (!LottieInspector.TryInspect(json, side, out var duration, out var fps))
            return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, ExpressionFormat.Lottie);

        if (json == data)
        {
            reencoded = LottieInspector.Gzip(data);
            if (reencoded.Length > cap)
                return ExpressionValidation.Failed(ExpressionError.TOO_LARGE, ExpressionFormat.Lottie);
        }

        return new ExpressionValidation(true, ExpressionError.NONE, ExpressionFormat.Lottie, side, side,
            reencoded?.Length ?? data.Length, contentType, reencoded, reencoded is null ? null : ExpressionContentTypes.Tgs,
            null, duration, fps);
    }

    private static ExpressionValidation ValidateVideo(byte[] data, ExpressionKind kind, int cap)
    {
        if (data.Length > cap)
            return ExpressionValidation.Failed(ExpressionError.TOO_LARGE, ExpressionFormat.Video);

        if (!WebmProbe.TryRead(data, out var info)
         || info.TrackCount != 1
         || info.TrackType != 1
         || !info.IsVp9
         || info.Frames == 0
         || info.ForeignBlocks
         || !ExpressionLimits.DimensionsFit(kind, ExpressionFormat.Video, info.Width, info.Height)
         || info.DurationSeconds > ExpressionLimits.MaxDurationSeconds + info.SlackSeconds
                                 + Math.Min(info.FrameSeconds, ExpressionLimits.VideoMaxOverrunSeconds)
         || info.Fps > ExpressionLimits.VideoMaxFps + ExpressionLimits.VideoFpsTolerance)
            return ExpressionValidation.Failed(ExpressionError.INVALID_FORMAT, ExpressionFormat.Video);

        return new ExpressionValidation(true, ExpressionError.NONE, ExpressionFormat.Video, info.Width, info.Height,
            data.Length, ExpressionContentTypes.Webm, null, null, null, info.DurationSeconds, info.Fps);
    }

    private static bool IsPng(ReadOnlySpan<byte> data)
        => data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    private static bool IsWebp(ReadOnlySpan<byte> data)
        => data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);

    /// <summary>The whole stream, or null once it passes <paramref name="limit"/> bytes.</summary>
    private static async ValueTask<byte[]?> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        if (stream.CanSeek && stream.Length - stream.Position > limit)
            return null;

        using var buffer = new MemoryStream(stream.CanSeek ? (int)(stream.Length - stream.Position) : 0);
        var       chunk  = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > limit)
                    return null;
                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }
}
