namespace ArgonSharedLogicTest.Expressions;

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

internal static class TestImages
{
    public static readonly Rgba32 Clear = new(0, 0, 0, 0);
    public static readonly Rgba32 Ink   = new(220, 40, 90, 255);

    /// <summary>A filled disc on a transparent canvas; pixels are either fully clear or fully opaque.</summary>
    public static Image<Rgba32> Disc(int width, int height, double cx, double cy, double radius, double hole = 0)
    {
        var image = new Image<Rgba32>(width, height, Clear);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
            if (d <= radius && d >= hole)
                image[x, y] = Ink;
        }
        return image;
    }

    public static Image<Rgba32> Disc(int width, int height)
        => Disc(width, height, width / 2.0, height / 2.0, Math.Min(width, height) * 0.4);

    public static Image<Rgba32> Noise(int width, int height, int seed)
    {
        var random = new Random(seed);
        var image  = new Image<Rgba32>(width, height);
        var bytes  = new byte[4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            random.NextBytes(bytes);
            image[x, y] = new Rgba32(bytes[0], bytes[1], bytes[2], 255);
        }
        return image;
    }

    public static byte[] Png(Image image)
    {
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    public static byte[] Webp(Image image, bool lossless = true)
    {
        using var output = new MemoryStream();
        image.SaveAsWebp(output, new WebpEncoder { FileFormat = lossless ? WebpFileFormatType.Lossless : WebpFileFormatType.Lossy });
        return output.ToArray();
    }

    public static byte[] Jpeg(Image image)
    {
        using var output = new MemoryStream();
        image.SaveAsJpeg(output);
        return output.ToArray();
    }

    public static byte[] Alpha(Image<Rgba32> image)
    {
        var alpha = new byte[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            alpha[y * image.Width + x] = image[x, y].A;
        return alpha;
    }
}

internal static class TestLottie
{
    /// <summary>A 3 s, 60 fps, 512×512 animation: one shape layer with a rectangle and a keyframed rotation.</summary>
    public static JsonObject Document(int side = 512)
        => (JsonObject)JsonNode.Parse($$"""
        {
          "v": "5.7.4", "fr": 60, "ip": 0, "op": 180, "w": {{side}}, "h": {{side}}, "nm": "test", "ddd": 0,
          "assets": [],
          "layers": [
            {
              "ddd": 0, "ind": 1, "ty": 4, "nm": "rect", "sr": 1, "ao": 0,
              "ks": {
                "o": { "a": 0, "k": 100 },
                "r": { "a": 1, "k": [
                  { "i": { "x": [0.833], "y": [0.833] }, "o": { "x": [0.167], "y": [0.167] }, "t": 0, "s": [0] },
                  { "t": 179, "s": [360] }
                ] },
                "p": { "a": 0, "k": [256, 256, 0] },
                "a": { "a": 0, "k": [0, 0, 0] },
                "s": { "a": 0, "k": [100, 100, 100] }
              },
              "shapes": [
                {
                  "ty": "gr", "nm": "group",
                  "it": [
                    { "ty": "rc", "d": 1, "s": { "a": 0, "k": [200, 200] }, "p": { "a": 0, "k": [0, 0] }, "r": { "a": 0, "k": 0 } },
                    { "ty": "fl", "c": { "a": 0, "k": [1, 0, 0, 1] }, "o": { "a": 0, "k": 100 } },
                    { "ty": "tr", "p": { "a": 0, "k": [0, 0] }, "a": { "a": 0, "k": [0, 0] }, "s": { "a": 0, "k": [100, 100] },
                      "r": { "a": 0, "k": 0 }, "o": { "a": 0, "k": 100 } }
                  ]
                }
              ],
              "ip": 0, "op": 180, "st": 0, "bm": 0
            }
          ]
        }
        """)!;

    public static JsonObject Layer(JsonObject document) => document["layers"]![0]!.AsObject();

    public static JsonArray GroupItems(JsonObject document) => Layer(document)["shapes"]![0]!["it"]!.AsArray();

    public static byte[] Json(JsonNode document) => Encoding.UTF8.GetBytes(document.ToJsonString());

    public static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(data);
        return output.ToArray();
    }

    public static byte[] Gunzip(byte[] data)
    {
        using var gzip   = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>Parses the subset of SVG the tracer writes: <c>Mx,y</c>, <c>l</c> with relative pairs, <c>z</c>.</summary>
internal static class OutlinePath
{
    public static List<List<(int X, int Y)>> Parse(string path)
    {
        var contours = new List<List<(int X, int Y)>>();
        var numbers  = new List<int>();
        var command  = '\0';
        var i        = 0;

        while (i <= path.Length)
        {
            var c = i < path.Length ? path[i] : 'z';

            if (char.IsAsciiLetter(c) || i == path.Length)
            {
                Flush(command, numbers, contours);
                numbers.Clear();
                command = c;
                i++;
                continue;
            }

            if (c == ',')
            {
                i++;
                continue;
            }

            var start = i;
            if (c == '-')
                i++;
            while (i < path.Length && char.IsAsciiDigit(path[i]))
                i++;
            numbers.Add(int.Parse(path.AsSpan(start, i - start)));
        }

        return contours;

        static void Flush(char command, List<int> numbers, List<List<(int X, int Y)>> contours)
        {
            switch (command)
            {
                case 'M':
                    Assert.That(numbers, Has.Count.EqualTo(2), "M takes one absolute point");
                    contours.Add([(numbers[0], numbers[1])]);
                    break;
                case 'l':
                    Assert.That(numbers.Count % 2, Is.Zero, "l takes pairs");
                    var contour = contours[^1];
                    for (var k = 0; k < numbers.Count; k += 2)
                        contour.Add((contour[^1].X + numbers[k], contour[^1].Y + numbers[k + 1]));
                    break;
            }
        }
    }

    public static double SignedArea(List<(int X, int Y)> ring)
    {
        var sum = 0.0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            sum += (double)ring[j].X * ring[i].Y - (double)ring[i].X * ring[j].Y;
        return sum / 2;
    }
}

/// <summary>A stream that cannot seek, so the validator reads it the way it reads a network body.</summary>
internal sealed class ForwardOnlyStream(byte[] data) : Stream
{
    private readonly MemoryStream inner = new(data);

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1000));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
