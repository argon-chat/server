namespace Argon.Shared;

using System.Diagnostics;
using System.Runtime.CompilerServices;

public static class Ensure
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void That(bool condition, [CallerArgumentExpression("condition")] string? message = null)
    {
        if (!condition)
            throw new InvalidOperationException(message ?? "Assertion failed.");
    }
}