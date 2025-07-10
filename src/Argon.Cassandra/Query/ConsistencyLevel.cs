namespace Argon.Cassandra.Query;

/// <summary>
/// Consistency levels supported by Cassandra
/// </summary>
public enum ConsistencyLevel
{
    Any,
    One,
    Two,
    Three,
    Quorum,
    All,
    LocalQuorum,
    EachQuorum,
    Serial,
    LocalSerial,
    LocalOne
}