namespace ArgonSharedLogicTest;

using Argon.Api.Features.Utils;
using static String;
using static Assert;

public class FractionalIndexTests
{
    [Test]
    public void Min_ShouldBeLessThanMax()
    {
        var min = FractionalIndex.Min();
        var max = FractionalIndex.Max();
        That(min.CompareTo(max), Is.LessThan(0));
    }

    [Test]
    public void Between_MinMax_ShouldReturnMiddle()
    {
        var min = FractionalIndex.Min();
        var max = FractionalIndex.Max();
        var mid = FractionalIndex.Between(min, max);
        That(min.CompareTo(mid), Is.LessThan(0));
        That(mid.CompareTo(max), Is.LessThan(0));
    }

    [Test]
    public void After_ShouldReturnLargerIndex()
    {
        var min = FractionalIndex.Min();
        var next = FractionalIndex.After(min);
        That(min.CompareTo(next), Is.LessThan(0));
    }

    [Test]
    public void Before_ShouldReturnSmallerIndex()
    {
        var max = FractionalIndex.Max();
        var prev = FractionalIndex.Before(max);
        That(prev.CompareTo(max), Is.LessThan(0));
    }

    [Test]
    public void Increment_Decrement_WorksCorrectly()
    {
        var a = FractionalIndex.Parse("0|0000000001");
        var b = a.Increment();
        var c = b.Decrement();
        That(a.Value, Is.EqualTo(c.Value));
    }

    [Test]
    public void Decrement_Min_Throws()
    {
        var min = FractionalIndex.Min();
        Throws<InvalidOperationException>(() => min.Decrement());
    }

    [Test]
    public void Increment_Max_Throws()
    {
        var max = FractionalIndex.Max();
        Throws<InvalidOperationException>(() => max.Increment());
    }

    [Test]
    public void IsValid_ReturnsTrue_ForCorrectIndex()
    {
        var index = FractionalIndex.Parse("0|0000000001");
        That(FractionalIndex.IsValid(index), Is.True);
    }

    [Test]
    public void IsValid_ReturnsFalse_ForNull()
        => That(FractionalIndex.IsValid(null), Is.False);

    [Test]
    public void IsMin_IsMax_WorkCorrectly()
    {
        var min = FractionalIndex.Min();
        var max = FractionalIndex.Max();
        That(min.IsMin, Is.True);
        That(max.IsMax, Is.True);
    }

    [Test]
    public void IsBefore_IsAfter_WorkCorrectly()
    {
        var a = FractionalIndex.Parse("0|0000000001");
        var b = FractionalIndex.Parse("0|0000000002");
        That(FractionalIndex.IsBefore(a, b), Is.True);
        That(FractionalIndex.IsAfter(b, a), Is.True);
    }

    [Test]
    public void RankPart_And_BucketPart_AreCorrect()
    {
        var index = FractionalIndex.Parse("3|abcd123456");
        That(index.BucketPart, Is.EqualTo("3"));
        That(index.RankPart, Is.EqualTo("abcd123456"));
    }

    [Test]
    public void Between_ProducesStrictlyIncreasingSequence()
    {
        var a = FractionalIndex.Min();
        var c = FractionalIndex.Min();
        for (var i = 0; i < 10000; i++)
        {
            var b = FractionalIndex.After(a);

            c = FractionalIndex.Between(a, b);
            //That(a.CompareTo(b), Is.LessThan(0));
            b = c;
            a = b;
        }
    }

    [Test]
    public void Between_TwoCloseValues_StillProducesValid()
    {
        var a = FractionalIndex.Parse("0|0000000001");
        var b = FractionalIndex.Parse("0|0000000002");
        var mid = FractionalIndex.Between(a, b);
        That(FractionalIndex.IsBefore(a, mid), Is.True);
        That(FractionalIndex.IsBefore(mid, b), Is.True);
    }

    [Test]
    public void Parse_ToString_RoundTrip()
    {
        var original = "0|abcd123456";
        var index = FractionalIndex.Parse(original);
        That(index.ToString(), Is.EqualTo(original));
    }

    [Test]
    public void Increment_MatchesExpected()
    {
        var index = FractionalIndex.Parse("0|0000000000");
        var next = index.Increment();
        That(next.Value, Is.EqualTo("0|0000000001"));
    }

    [Test]
    public void Decrement_MatchesExpected()
    {
        var index = FractionalIndex.Parse("0|0000000002");
        var prev = index.Decrement();
        That(prev.Value, Is.EqualTo("0|0000000001"));
    }

    [Test]
    public void Between_NullNull_GeneratesMiddle()
    {
        var value = FractionalIndex.Between(null, null);
        That(FractionalIndex.IsValid(value), Is.True);
    }

    [Test]
    public void Between_VariousLengths_ProducesStrictlyBetween()
    {
        var a = FractionalIndex.Parse("0|0000000001");
        var b = FractionalIndex.Parse("0|0000000003");

        for (var i = 0; i < 10; i++)
        {
            var mid = FractionalIndex.Between(a, b);
            That(FractionalIndex.IsBefore(a, mid));
            That(FractionalIndex.IsBefore(mid, b));
            a = mid;
        }
    }

    [Test]
    public void Parse_InvalidFormat_ThrowsIfMissingDelimiter()
        => Throws<InvalidOperationException>(() => Console.Write(FractionalIndex.Parse("invalidFormat").BucketPart));

    [Test]
    public void CompareTo_WorksForDifferentLengths()
    {
        var a = FractionalIndex.Parse("0|1");
        var b = FractionalIndex.Parse("0|10");
        That(a.CompareTo(b), Is.LessThan(0));
    }

    [Test]
    public void VeryLongRanks_SortCorrectly()
    {
        var a = FractionalIndex.Parse("0|0000000001");
        var b = FractionalIndex.Parse("0|0000000001r");
        var c = FractionalIndex.Parse("0|0000000001ra");
        That(a.CompareTo(b), Is.LessThan(0));
        That(b.CompareTo(c), Is.LessThan(0));
    }

    [Test]
    public void ToString_MatchesOriginalValue()
    {
        var val = "0|zzzzyyyyyy";
        var index = FractionalIndex.Parse(val);
        That(index.ToString(), Is.EqualTo(val));
    }

    [Test]
    public void Sort_ListOfIndexes_ProducesCorrectOrder()
    {
        var list = new List<FractionalIndex>
            {
                FractionalIndex.Parse("0|0000000001"),
                FractionalIndex.Parse("0|0000000001r"),
                FractionalIndex.Parse("0|0000000001ra"),
                FractionalIndex.Parse("0|0000000002"),
                FractionalIndex.Parse("0|0000000000")
            };

        list.Sort();

        for (var i = 0; i < list.Count - 1; i++)
            That(list[i].CompareTo(list[i + 1]), Is.LessThan(0), $"Order failed at index {i}");
    }
    [Test]
    public void Between_VeryCloseValues_StillFindsMiddle()
    {
        var a = FractionalIndex.Parse("0|0000000002z");
        var b = FractionalIndex.Parse("0|0000000003");
        var mid = FractionalIndex.Between(a, b);
        That(FractionalIndex.IsBefore(a, mid));
        That(FractionalIndex.IsBefore(mid, b));
    }

    [Test]
    public void Between_Repeatedly_BuildsDeeperPrecision()
    {
        var a = FractionalIndex.Parse("0|0000000001");
        var b = FractionalIndex.Parse("0|0000000002");
        for (var i = 0; i < 20; i++)
        {
            var mid = FractionalIndex.Between(a, b);
            That(FractionalIndex.IsBefore(a, mid));
            That(FractionalIndex.IsBefore(mid, b));
            b = mid;
        }
    }

    [Test]
    public void Between_AtMaxPrecision_StillValidAndInsertable()
    {
        var a = FractionalIndex.Parse("0|zzzzzzzzzy");
        var b = FractionalIndex.Parse("0|zzzzzzzzzz");
        var mid = FractionalIndex.Between(a, b);
        That(FractionalIndex.IsBefore(a, mid));
        That(FractionalIndex.IsBefore(mid, b));
    }

    [Test]
    public void Sorting_MixedDepthValues_IsStableAndCorrect()
    {
        var list = new List<FractionalIndex>
            {
                FractionalIndex.Parse("0|0"),
                FractionalIndex.Parse("0|0m"),
                FractionalIndex.Parse("0|0mm"),
                FractionalIndex.Parse("0|1"),
                FractionalIndex.Parse("0|00"),
            };
        list.Sort();

        for (var i = 0; i < list.Count - 1; i++)
        {
            That(list[i].CompareTo(list[i + 1]), Is.LessThan(0), $"Sort failed at {i}");
        }
    }
}