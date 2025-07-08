namespace Argon.Cassandra.Core;

public enum EntityState
{
    Modified = 0,
    Added = 1,
    Deleted = 2,
    Detached = 3,
    Unchanged = 4
}