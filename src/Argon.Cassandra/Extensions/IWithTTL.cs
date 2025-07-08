namespace Argon.Cassandra.Extensions;

public interface IWithTTL
{
    int TtlSeconds { get; set; }
}