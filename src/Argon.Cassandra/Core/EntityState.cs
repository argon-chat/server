namespace Argon.Cassandra.Core;

public enum EntityState
{
    Modified = 0,
    Added,
    Deleted,
    Detached,
    Unchanged 
}