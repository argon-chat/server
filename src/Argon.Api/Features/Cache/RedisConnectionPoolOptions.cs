namespace Argon.Services;

public record RedisConnectionPoolOptions
{
    public uint MaxSize { get; set; } = 16;
}