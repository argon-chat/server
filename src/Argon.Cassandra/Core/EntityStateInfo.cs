namespace Argon.Cassandra.Core;

public record EntityStateInfo(EntityState state, int? TimeToLive = null)
{
    public static implicit operator EntityState(EntityStateInfo info) => info.state;
}