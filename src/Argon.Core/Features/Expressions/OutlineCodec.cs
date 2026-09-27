namespace Argon.Features.Expressions;

/// <summary>Telegram's <c>photoPathSize</c> byte format for sticker outlines (tweb <c>getPathFromBytes</c>).</summary>
public static class OutlineCodec
{
    private const string Table = "AACAAAAHAAALMAAAQASTAVAAAZaacaaaahaaalmaaaqastava.az0123456789-,";

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var path = new StringBuilder(bytes.Length * 2 + 2);
        path.Append('M');

        foreach (var b in bytes)
        {
            if (b >= 192)
            {
                path.Append(Table[b - 192]);
                continue;
            }

            if (b >= 128)
                path.Append(',');
            else if (b >= 64)
                path.Append('-');

            AppendSmall(path, b & 63);
        }

        path.Append('z');
        return path.ToString();
    }

    /// <summary>
    /// Inverse of <see cref="Decode"/>. The path must start with <c>M</c> and end with <c>z</c>; both are
    /// implied by the format and not stored.
    /// </summary>
    public static byte[] Encode(string svgPath)
    {
        ArgumentNullException.ThrowIfNull(svgPath);

        if (svgPath.Length < 2 || svgPath[0] != 'M' || svgPath[^1] != 'z')
            throw new ArgumentException("An outline path starts with 'M' and ends with 'z'.", nameof(svgPath));

        var body   = svgPath.AsSpan(1, svgPath.Length - 2);
        var output = new List<byte>(body.Length);
        var i      = 0;

        while (i < body.Length)
        {
            var c = body[i];

            if (c is ',' or '-' && i + 1 < body.Length && char.IsAsciiDigit(body[i + 1]))
            {
                var start = ++i;
                while (i < body.Length && char.IsAsciiDigit(body[i]))
                    i++;
                EmitDigits(output, body[start..i], c == ',' ? 128 : 64);
            }
            else if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < body.Length && char.IsAsciiDigit(body[i]))
                    i++;
                EmitDigits(output, body[start..i], 0);
            }
            else
            {
                var index = Table.IndexOf(c);
                if (index < 0)
                    throw new ArgumentException($"'{c}' cannot be encoded in an outline path.", nameof(svgPath));
                output.Add((byte)(192 + index));
                i++;
            }
        }

        return output.ToArray();
    }

    // Decoded numbers concatenate, so a digit run is split into chunks of 0..63 without leading zeros
    // ("512" -> 51, 2); only the first chunk carries the ',' or '-' prefix.
    private static void EmitDigits(List<byte> output, ReadOnlySpan<char> digits, int prefix)
    {
        var i = 0;

        while (i < digits.Length)
        {
            var value  = digits[i] - '0';
            var length = 1;

            if (value != 0)
            {
                while (i + length < digits.Length)
                {
                    var next = value * 10 + (digits[i + length] - '0');
                    if (next > 63)
                        break;
                    value = next;
                    length++;
                }
            }

            output.Add((byte)(prefix | value));
            prefix =  0;
            i      += length;
        }
    }

    private static void AppendSmall(StringBuilder path, int value)
    {
        if (value >= 10)
            path.Append((char)('0' + value / 10));
        path.Append((char)('0' + value % 10));
    }
}
