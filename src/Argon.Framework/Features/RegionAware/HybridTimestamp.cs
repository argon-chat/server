namespace Argon.Features.RegionAware;

using JetBrains.Annotations;

[UsedImplicitly, PublicAPI]
public readonly struct HybridTimestamp(long physicalMillis, int logicalCounter, string nodeId)
    : IComparable<HybridTimestamp>, IEquatable<HybridTimestamp>
{
    public long   PhysicalMillis { get; } = physicalMillis;
    public int    LogicalCounter { get; } = logicalCounter;
    public string NodeId         { get; } = nodeId ?? throw new ArgumentNullException(nameof(nodeId));

    public int CompareTo(HybridTimestamp other)
    {
        var cmp = PhysicalMillis.CompareTo(other.PhysicalMillis);
        if (cmp != 0) return cmp;

        cmp = LogicalCounter.CompareTo(other.LogicalCounter);
        if (cmp != 0) return cmp;

        return string.CompareOrdinal(NodeId, other.NodeId);
    }

    public bool Equals(HybridTimestamp other) =>
        PhysicalMillis == other.PhysicalMillis &&
        LogicalCounter == other.LogicalCounter &&
        string.Equals(NodeId, other.NodeId, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is HybridTimestamp ts && Equals(ts);

    public override int GetHashCode() =>
        HashCode.Combine(PhysicalMillis, LogicalCounter, NodeId);

    public static bool operator <(HybridTimestamp left, HybridTimestamp right)  => left.CompareTo(right) < 0;
    public static bool operator >(HybridTimestamp left, HybridTimestamp right)  => left.CompareTo(right) > 0;
    public static bool operator <=(HybridTimestamp left, HybridTimestamp right) => left.CompareTo(right) <= 0;
    public static bool operator >=(HybridTimestamp left, HybridTimestamp right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        $"{PhysicalMillis}:{LogicalCounter}:{NodeId}";
}