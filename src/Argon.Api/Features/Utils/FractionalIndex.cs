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
        var ra = a.RankPart;
        var rb = b.RankPart;

        var mid = MiddleString(ra, rb, RankLength);
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

    private static string MiddleString(string a, string b, int _)
    {
        if (a == b)
            throw new ArgumentException("Cannot generate between identical strings");

        var i      = 0;
        var result = "";

        while (true)
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
    }

    public FractionalIndex Increment()
    {
        var rank   = RankPart;
        var bucket = BucketPart;

        var n = Base36ToBigInt(rank);
        if (n >= Base36ToBigInt(new string('z', RankLength)))
            throw new InvalidOperationException("Cannot increment beyond max");

        var next = BigIntToBase36(n + 1).PadLeft(RankLength, '0');
        return new FractionalIndex($"{bucket}|{next}");
    }


    public FractionalIndex Decrement()
    {
        var rank   = RankPart;
        var bucket = BucketPart;

        var n = Base36ToBigInt(rank);
        if (n == 0)
            throw new InvalidOperationException("Cannot decrement beyond min");

        var prev = BigIntToBase36(n - 1).PadLeft(RankLength, '0');
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


}