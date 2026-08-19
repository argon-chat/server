namespace Argon.Features.Aegis;

/// <summary>
/// Comparing a presented client secret against the registered one.
/// </summary>
/// <remarks>
/// Fixed-time, because a comparison that returns as soon as two bytes differ tells whoever is
/// guessing how much of the secret they got right, one request at a time. <c>==</c> on
/// <see cref="string"/> does exactly that.
/// <para>
/// The buffers are on the stack and the secrets are wiped out of them before the frame goes, so a
/// credential does not sit in the heap waiting for a collection that may be a long way off. A secret
/// longer than <see cref="MaxSecretBytes"/> is refused rather than allowed to size the buffer.
/// </para>
/// </remarks>
public static class ClientSecret
{
    private const int MaxSecretBytes = 512;

    public static bool Matches(string? expected, string? actual)
    {
        if (expected is null || actual is null)
            return false;

        Span<byte> expectedBytes = stackalloc byte[MaxSecretBytes];
        Span<byte> actualBytes   = stackalloc byte[MaxSecretBytes];

        try
        {
            return TryEncode(expected, expectedBytes, out var expectedLength)
                && TryEncode(actual, actualBytes, out var actualLength)
                && CryptographicOperations.FixedTimeEquals(
                       expectedBytes[..expectedLength], actualBytes[..actualLength]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }

    private static bool TryEncode(ReadOnlySpan<char> value, Span<byte> destination, out int written)
    {
        written = 0;

        return Encoding.UTF8.GetByteCount(value) <= destination.Length
            && Encoding.UTF8.TryGetBytes(value, destination, out written);
    }
}
