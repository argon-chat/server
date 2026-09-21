namespace Argon.Features.Cosmetics;

using System.Numerics;

/// <summary>
/// The checks every payload with geometry in it makes, written once.
/// </summary>
/// <remarks>
/// Four numbers for four sides, a bounded number, a required bounded number. Each kind used to carry
/// its own copy, and a copy is where an off-by-one gets fixed in one payload and not the other —
/// with each file's tests seeing only their own.
/// </remarks>
public static class CosmeticPayloadChecks
{
    /// <summary>Top, right, bottom, left — the order CSS states them in, and the one an operator expects.</summary>
    public const int Sides = 4;

    public static void FourSides(
        ICosmeticPayloadReport report,
        int[]? sides,
        string at,
        int low,
        int high,
        bool required)
    {
        if (sides is null)
        {
            if (required)
                report.Error($"{at} is missing; it is four numbers — top, right, bottom, left");

            return;
        }

        if (sides.Length is not Sides)
        {
            report.Error($"{at} is four numbers — top, right, bottom, left — and has {sides.Length}");
            return;
        }

        for (var side = 0; side < Sides; side++)
        {
            if (sides[side] < low || sides[side] > high)
                report.Error($"{at}[{side}] is {sides[side]}, and the range is {low} to {high}");
        }
    }

    /// <summary>A value that has to be there, returned when it is and null when it is not.</summary>
    public static T? Require<T>(ICosmeticPayloadReport report, T? value, string at, T low, T high)
        where T : struct, INumber<T>
    {
        if (value is not { } number)
        {
            report.Error($"{at} is missing");
            return null;
        }

        if (number < low || number > high)
        {
            report.Error($"{at} is {number}, and the range is {low} to {high}");
            return null;
        }

        return number;
    }

    /// <summary>A value that may be absent, checked when it is not.</summary>
    public static void Bound<T>(ICosmeticPayloadReport report, T? value, string at, T low, T high)
        where T : struct, INumber<T>
    {
        if (value is { } number && (number < low || number > high))
            report.Error($"{at} is {number}, and the range is {low} to {high}");
    }
}
