namespace Argon.Core.Features.Utils;

public static class GuidBCD
{
    public static Guid EncodePhoneToGuid(ulong header, string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());

        if (digits.Length > 15)
            throw new ArgumentException("Phone number exceeds E.164 limit (15 digits).");

        Span<byte> guidBytes = stackalloc byte[16];

        BitConverter.TryWriteBytes(guidBytes[..8], header);

        var phoneBytes = guidBytes[8..];

        var byteIndex = 0;
        for (var i = 0; i < digits.Length; i += 2)
        {
            var high = (byte)(digits[i] - '0');
            var low = (i + 1 < digits.Length)
                ? (byte)(digits[i + 1] - '0')
                : (byte)0xF;

            phoneBytes[byteIndex++] = (byte)((high << 4) | low);
        }

        for (; byteIndex < 8; byteIndex++)
            phoneBytes[byteIndex] = 0xFF;

        return new Guid(guidBytes);
    }

    public static(ulong header, string phone) DecodePhoneFromGuid(Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);

        var      header     = BitConverter.ToUInt64(bytes[..8]);
        var phoneBytes = bytes[8..];

        var sb = new StringBuilder();

        for (var i = 0; i < 8; i++)
        {
            var b    = phoneBytes[i];
            var  high = (b >> 4) & 0xF;
            var  low  = b & 0xF;

            if (high <= 9) sb.Append((char)('0' + high));
            if (low <= 9) sb.Append((char)('0' + low));
        }

        return (header, sb.ToString());
    }
}