namespace Argon.Services;

public interface IRedisPoolConnections : IHostedService
{
    ConnectionScope Rent();
}