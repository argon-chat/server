namespace Argon.Features.Expressions;

using System.Runtime.InteropServices;
using Native;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

/// <summary>Draws the first frame of an animated expression on the server, so its thumbnail and moderation need no client.</summary>
public interface IFirstFrameRenderer
{
    /// <summary>Whether the native library for the format loaded; without it the client's thumbnail is required.</summary>
    bool IsAvailable(ExpressionFormat format);

    /// <summary>Frame 0 as straight RGBA at the item's size, or null — for unreadable input too, never an exception.</summary>
    Task<Image<Rgba32>?> RenderAsync(ReadOnlyMemory<byte> file, ExpressionFormat format, int width, int height, CancellationToken ct);
}

/// <summary>rlottie for Lottie/TGS and libvpx for VP9 WEBM, called in-process on the thread pool.</summary>
public sealed class FirstFrameRenderer : IFirstFrameRenderer
{
    public const int MaxSide = 512;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static readonly int[] ThumbQualities = [90, 80, 70, 60, 50, 40, 30, 20];

    private readonly ILogger<FirstFrameRenderer> logger;

    // Released when the native call returns, not at the timeout: a hung render keeps its slot.
    private readonly SemaphoreSlim slots = new(Math.Max(1, Environment.ProcessorCount / 2));

    public FirstFrameRenderer(ILogger<FirstFrameRenderer> logger)
    {
        this.logger = logger;

        logger.LogInformation("First frames of animated expressions: Lottie {Lottie}, VP9 {Vp9}",
            RlottieNative.IsAvailable ? "rlottie" : "unavailable", VpxNative.Version ?? "unavailable");
    }

    public bool IsAvailable(ExpressionFormat format)
        => format switch
        {
            ExpressionFormat.Lottie => RlottieNative.IsAvailable,
            ExpressionFormat.Video  => VpxNative.IsAvailable,
            _                       => false
        };

    public async Task<Image<Rgba32>?> RenderAsync(ReadOnlyMemory<byte> file, ExpressionFormat format, int width, int height,
        CancellationToken ct)
    {
        if (!IsAvailable(format) || width is < 1 or > MaxSide || height is < 1 or > MaxSide)
            return null;

        if (!await slots.WaitAsync(Timeout, ct))
        {
            logger.LogWarning("No slot to render a {Format} first frame within {Timeout}", format, Timeout);
            return null;
        }

        var work = Task.Run(() =>
        {
            try
            {
                return Render(file, format, width, height);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not render a {Format} first frame", format);
                return null;
            }
            finally
            {
                slots.Release();
            }
        }, CancellationToken.None);

        try
        {
            return await work.WaitAsync(Timeout, ct);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("A {Format} first frame took longer than {Timeout}", format, Timeout);
            _ = DisposeLateAsync(work);
            return null;
        }
    }

    /// <summary>A lossy WEBP within <see cref="ExpressionLimits.ThumbMaxBytes"/>, quality stepping down; null if none fits.</summary>
    public static byte[]? EncodeThumb(Image<Rgba32> frame)
    {
        foreach (var quality in ThumbQualities)
        {
            using var output = new MemoryStream();
            frame.SaveAsWebp(output, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = quality });

            if (output.Length <= ExpressionLimits.ThumbMaxBytes)
                return output.ToArray();
        }

        return null;
    }

    private static Image<Rgba32>? Render(ReadOnlyMemory<byte> file, ExpressionFormat format, int width, int height)
    {
        var frame = format switch
        {
            ExpressionFormat.Lottie => Json(file) is { } json ? RlottieNative.RenderFirstFrame(json, width, height) : null,
            ExpressionFormat.Video  => WebmProbe.TryReadFirstFrame(file.Span, out var at)
                ? VpxNative.DecodeFrame(at.Colour(file.Span), at.Alpha(file.Span), MaxSide)
                : null,
            _ => null
        };

        if (frame is not null && (frame.Width != width || frame.Height != height))
            frame.Mutate(x => x.Resize(width, height));

        return frame;
    }

    /// <summary>The Lottie JSON, gunzipped from a TGS and without a BOM, within <see cref="ExpressionLimits.LottieMaxJsonBytes"/>.</summary>
    private static byte[]? Json(ReadOnlyMemory<byte> file)
    {
        var data = MemoryMarshal.TryGetArray(file, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : file.ToArray();

        var json = LottieInspector.IsGzip(data)
            ? LottieInspector.Gunzip(data, ExpressionLimits.LottieMaxJsonBytes, out _)
            : data.Length <= ExpressionLimits.LottieMaxJsonBytes ? data : null;

        return json is [0xEF, 0xBB, 0xBF, ..] ? json[3..] : json;
    }

    private static async Task DisposeLateAsync(Task<Image<Rgba32>?> work)
        => (await work)?.Dispose();
}
