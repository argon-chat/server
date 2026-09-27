namespace Argon.Features.Expressions;

using System.IO.Compression;
using System.Text.Json;

/// <summary>Lottie/TGS checks: canvas, frame rate, duration and the features Telegram forbids.</summary>
internal static class LottieInspector
{
    private enum Scope : byte
    {
        Other,
        Root,
        Layer,
        Asset,
        Shape
    }

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = ExpressionLimits.LottieMaxDepth };

    public static bool IsGzip(ReadOnlySpan<byte> data)
        => data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;

    public static bool LooksLikeJson(ReadOnlySpan<byte> data)
    {
        data = SkipBom(data);
        foreach (var b in data)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                continue;
            return b == (byte)'{';
        }
        return false;
    }

    /// <summary>Null when the stream is not gzip or inflates past <paramref name="limit"/> (then <paramref name="tooLarge"/>).</summary>
    public static byte[]? Gunzip(byte[] data, int limit, out bool tooLarge)
    {
        tooLarge = false;

        try
        {
            using var gzip   = new GZipStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var       chunk  = new byte[16 * 1024];
            int       read;

            while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (output.Length + read > limit)
                {
                    tooLarge = true;
                    return null;
                }
                output.Write(chunk, 0, read);
            }

            return output.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    public static byte[] Gzip(ReadOnlySpan<byte> json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(json);
        return output.ToArray();
    }

    public static bool TryInspect(ReadOnlyMemory<byte> json, int side, out double durationSeconds, out double fps)
    {
        durationSeconds = 0;
        fps             = 0;

        try
        {
            using var document = JsonDocument.Parse(json[(json.Length - SkipBom(json.Span).Length)..], ParseOptions);
            var       root     = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (!Number(root, "w", out var w) || !Number(root, "h", out var h) || w != side || h != side)
                return false;
            if (!Number(root, "fr", out var fr) || !(fr > 0) || fr > ExpressionLimits.LottieMaxFps)
                return false;
            if (!Number(root, "ip", out var ip) || !Number(root, "op", out var op) || !(op > ip))
                return false;

            durationSeconds = (op - ip) / fr;
            fps             = fr;

            if (durationSeconds > ExpressionLimits.MaxDurationSeconds + 1e-9)
                return false;
            if (!root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
                return false;

            return Walk(root);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Iterative walk that stops at the first forbidden feature.</summary>
    private static bool Walk(JsonElement root)
    {
        var stack = new Stack<(JsonElement Element, Scope Scope)>();
        stack.Push((root, Scope.Root));

        while (stack.TryPop(out var item))
        {
            var (element, scope) = item;

            if (element.ValueKind == JsonValueKind.Array)
            {
                // Elements of an array share the array's scope ("layers": [...] holds layers).
                foreach (var child in element.EnumerateArray())
                    if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        stack.Push((child, scope));
                continue;
            }

            if (!Allowed(element, scope))
                return false;

            foreach (var property in element.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    stack.Push((property.Value, ChildScope(scope, property.Name)));
        }

        return true;
    }

    private static Scope ChildScope(Scope parent, string name)
        => (parent, name) switch
        {
            (Scope.Root, "layers")  => Scope.Layer,
            (Scope.Root, "assets")  => Scope.Asset,
            (Scope.Asset, "layers") => Scope.Layer,
            (Scope.Layer, "shapes") => Scope.Shape,
            (Scope.Shape, "it")     => Scope.Shape,
            _                       => Scope.Other
        };

    private static bool Allowed(JsonElement obj, Scope scope)
    {
        // Expressions: an animated property carrying script in "x".
        if (obj.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.String)
            return false;

        switch (scope)
        {
            case Scope.Root:
                return !Flag(obj, "ddd");

            case Scope.Layer:
                // 1 solid, 2 image, 5 text, 6 audio, 13 camera.
                if (Number(obj, "ty", out var type) && type is 1 or 2 or 5 or 6 or 13)
                    return false;
                if (Flag(obj, "ddd") || Flag(obj, "ao") || Flag(obj, "hasMask"))
                    return false;
                if (obj.TryGetProperty("tm", out _))
                    return false;
                if (NonEmptyArray(obj, "masksProperties") || NonEmptyArray(obj, "ef"))
                    return false;
                // Time stretch.
                if (Number(obj, "sr", out var stretch) && stretch != 1)
                    return false;
                return true;

            case Scope.Asset:
                // Image assets carry a path ("p"), embedded ones also "e": 1; precomps carry "layers".
                return !obj.TryGetProperty("p", out _) && !Flag(obj, "e");

            case Scope.Shape:
                return !(obj.TryGetProperty("ty", out var shape)
                      && shape.ValueKind == JsonValueKind.String
                      && shape.GetString() is "gs" or "rp" or "mm" or "sr");

            default:
                return true;
        }
    }

    private static bool Number(JsonElement obj, string name, out double value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out value);
    }

    private static bool Flag(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p)
        && (p.ValueKind == JsonValueKind.True || (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v) && v != 0));

    private static bool NonEmptyArray(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0;

    private static ReadOnlySpan<byte> SkipBom(ReadOnlySpan<byte> data)
        => data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? data[3..] : data;
}
