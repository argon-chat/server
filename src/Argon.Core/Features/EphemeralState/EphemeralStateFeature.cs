namespace Argon.Features.EphemeralState;

public static class EphemeralStateFeature
{
    public static IServiceCollection AddEphemeralStateFeature(this WebApplicationBuilder builder)
    {
        var provider = builder.Configuration.GetValue<string>("EphemeralStore:Provider") ?? "Redis";

        switch (provider)
        {
            case "Aerospike":
                // Future: register Aerospike implementations
                // builder.Services.AddSingleton<IEphemeralStateStore, AerospikeEphemeralStateStore>();
                // builder.Services.AddSingleton<IRateLimiterService, AerospikeRateLimiterService>();
                throw new NotSupportedException(
                    "Aerospike provider is not yet implemented. Use 'Redis' provider.");
            case "Redis":
            default:
                builder.Services.AddSingleton<IEphemeralStateStore, RedisEphemeralStateStore>();
                builder.Services.AddSingleton<IRateLimiterService, RedisRateLimiterService>();
                break;
        }

        return builder.Services;
    }
}
