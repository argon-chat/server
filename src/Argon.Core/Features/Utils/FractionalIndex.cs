namespace Argon.Api.Features.Utils;

public record struct FractionalIndex(string Value) : IComparable, IComparable<FractionalIndex>
{
    private const int    RankLength = 10;
    private const string BaseChars  = "0123456789abcdefghijklmnopqrstuvwxyz";
    private const int    Base       = 36;

    public string BucketPart => Value.Split('|')[0];
    public string RankPart   => Value.Split('|')[1];
    public bool   IsMin      => Value == Min().Value;
    public bool   IsMax      => Value == Max().Value;


    public static bool IsValid(FractionalIndex? index)
    {
        if (index is null) return false;
        if (string.IsNullOrEmpty(index.Value.Value)) return false;

        var parts = index.Value.Value.Split('|');
        return parts is [_, { Length: >= 1 }] &&
               parts[0].All(char.IsDigit) &&
               parts[1].All(c => BaseChars.Contains(c));
    }


    public static FractionalIndex Parse(string value)
    {
        var result = new FractionalIndex(value);

        if (!IsValid(result))
            throw new InvalidOperationException();

        return result;
    }

    public static FractionalIndex Min()
        => new("0|" + new string('0', RankLength));

    public static FractionalIndex Max()
        => new("0|" + new string('z', RankLength));

    public static FractionalIndex Between(FractionalIndex a, FractionalIndex b)
    {
        if (a.CompareTo(b) >= 0)
            throw new ArgumentException("First index must be less than second index");

        var ra = a.RankPart;
        var rb = b.RankPart;

        var mid = MiddleString(ra, rb);
        if (mid == ra || mid == rb)
            throw new InvalidOperationException("Cannot generate between these two values — no space left");

        return new FractionalIndex($"{a.BucketPart}|{mid}");
    }

    public static FractionalIndex Between(FractionalIndex? a, FractionalIndex? b)
        => a switch
        {
            null when b == null => Between(Min(), Max()),
            null                => Before(b.Value),
            _                   => b == null ? After(a.Value) : Between(a.Value, b.Value)
        };

    public static FractionalIndex After(FractionalIndex a)
        => a.Increment();

    public static FractionalIndex Before(FractionalIndex b)
        => b.Decrement();

    public int CompareTo(FractionalIndex other)
        => string.Compare(Value, other.Value, StringComparison.Ordinal);

    public override string ToString() => Value;

    public int CompareTo(object? obj)
    {
        if (obj is FractionalIndex e1)
            return this.CompareTo(e1);
        return 0;
    }

    public static bool IsBefore(FractionalIndex a, FractionalIndex b)
        => a.CompareTo(b) < 0;

    public static bool IsAfter(FractionalIndex a, FractionalIndex b)
        => a.CompareTo(b) > 0;

    private static string MiddleString(string a, string b)
    {
        if (string.Compare(a, b, StringComparison.Ordinal) >= 0)
            throw new ArgumentException("First string must be lexicographically less than second string");

        var maxIterations = Math.Max(a.Length, b.Length) + 2;
        var i      = 0;
        var result = "";

        while (i < maxIterations)
        {
            var ca = i < a.Length ? BaseChars.IndexOf(a[i]) : 0;
            var cb = i < b.Length ? BaseChars.IndexOf(b[i]) : Base - 1;

            if (cb - ca > 1)
            {
                var mid = (ca + cb) / 2;
                result += BaseChars[mid];
                return result;
            }

            result += BaseChars[ca];
            i++;
        }

        throw new InvalidOperationException("Cannot generate middle string — exceeded maximum precision");
    }

    public FractionalIndex Increment()
    {
        var rank   = RankPart;
        var bucket = BucketPart;

        var chars = rank.ToCharArray();
        var carry = 1;
        for (var i = chars.Length - 1; i >= 0 && carry > 0; i--)
        {
            var val = BaseChars.IndexOf(chars[i]) + carry;
            if (val >= Base)
            {
                chars[i] = BaseChars[0];
                carry = 1;
            }
            else
            {
                chars[i] = BaseChars[val];
                carry = 0;
            }
        }

        if (carry > 0)
            throw new InvalidOperationException("Cannot increment beyond max");

        var next = new string(chars);
        return new FractionalIndex($"{bucket}|{next}");
    }


    public FractionalIndex Decrement()
    {
        var rank   = RankPart;
        var bucket = BucketPart;

        var chars = rank.ToCharArray();
        var borrow = 1;
        for (var i = chars.Length - 1; i >= 0 && borrow > 0; i--)
        {
            var val = BaseChars.IndexOf(chars[i]) - borrow;
            if (val < 0)
            {
                chars[i] = BaseChars[Base - 1];
                borrow = 1;
            }
            else
            {
                chars[i] = BaseChars[val];
                borrow = 0;
            }
        }

        if (borrow > 0)
            throw new InvalidOperationException("Cannot decrement beyond min");

        var prev = new string(chars);
        if (prev.All(c => c == '0'))
            throw new InvalidOperationException("Cannot decrement beyond min");

        return new FractionalIndex($"{bucket}|{prev}");
    }

    private static ulong Base36ToBigInt(string s)
    {
        ulong result = 0;
        foreach (var c in s)
        {
            result *= Base;
            result += (ulong)BaseChars.IndexOf(c);
        }

        return result;
    }

    private static string BigIntToBase36(ulong n)
    {
        if (n == 0) return "0";

        var result = "";
        while (n > 0)
        {
            result =  BaseChars[(int)(n % Base)] + result;
            n      /= Base;
        }

        return result;
    }

    /// <summary>
    /// Generates evenly-distributed FractionalIndex values across the available range.
    /// </summary>
    public static List<FractionalIndex> Distribute(int count)
    {
        if (count <= 0) return [];

        var maxVal = Base36ToBigInt(new string('z', RankLength));
        var step   = maxVal / (ulong)(count + 1);

        var result = new List<FractionalIndex>(count);
        for (var i = 0; i < count; i++)
        {
            var rank = BigIntToBase36(step * (ulong)(i + 1)).PadLeft(RankLength, '0');
            result.Add(new FractionalIndex($"0|{rank}"));
        }
        return result;
    }
}