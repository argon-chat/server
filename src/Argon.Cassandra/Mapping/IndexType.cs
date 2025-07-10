namespace Argon.Cassandra.Mapping;

/// <summary>
/// Specifies the type of index.
/// </summary>
public enum IndexType
{
    /// <summary>
    /// Default secondary index.
    /// </summary>
    Default,
    
    /// <summary>
    /// SASI (SSTable Attached Secondary Index).
    /// </summary>
    SASI,
    
    /// <summary>
    /// Custom index implementation.
    /// </summary>
    Custom
}