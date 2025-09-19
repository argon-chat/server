namespace Argon.Shared;

using System.Buffers.Binary;

public static class ArgonTimeExtensions
{
    private static readonly Lazy<DateTimeOffset> ArgonEpoch = new(() => DateTimeOffset.Parse("01.01.2025 00:00:00 +00:00"));

    public static uint ToArgonTimeSeconds(this DateTimeOffset dateTime)
        => (uint)(dateTime - ArgonEpoch.Value).TotalSeconds;

    public static Guid Pack(
        uint epochTimestamp,
        byte regionId,
        ushort bucketCode,
        ulong randomEntropy,
        byte reservedFlags = 0
    )
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(bytes[..4], epochTimestamp);

        bytes[4] = regionId;

        BinaryPrimitives.WriteUInt16BigEndian(bytes.Slice(5, 2), bucketCode);

        BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(7, 8), randomEntropy);

        var lastByte = (byte)(reservedFlags & 0x0F);

        byte checksum = 0;
        for (var i = 0; i < 15; i++)
            checksum ^= bytes[i];
        checksum &= 0x0F;

        bytes[15] = (byte)((checksum << 4) | lastByte);

        return new Guid(bytes);
    }
}