namespace Argon.Features.Integrations.Connections;

using Argon.Features.Integrations.Connections.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

/// <summary>The configured adapters, by kind.</summary>
public interface IConnectionProviderRegistry
{
    /// <summary>The adapter for a provider, or null when the deployment has no app registered for it.</summary>
    IConnectionProvider? Get(ConnectionProvider kind);

    IReadOnlyList<IConnectionProvider> Configured();
}

/// <summary>
/// Resolves adapters per call rather than holding them: each is a typed <c>HttpClient</c> service,
/// transient by design, so the factory's handler rotation applies to them.
/// </summary>
public sealed class ConnectionProviderRegistry(IServiceProvider services) : IConnectionProviderRegistry
{
    public IConnectionProvider? Get(ConnectionProvider kind)
        => services.GetServices<IConnectionProvider>().FirstOrDefault(p => p.Kind == kind && p.IsConfigured);

    public IReadOnlyList<IConnectionProvider> Configured()
        => services.GetServices<IConnectionProvider>().Where(p => p.IsConfigured).ToList();
}

public static class ConnectionsServiceCollectionExtensions
{
    /// <summary>The adapters, the sealer and the handshake store — the silo half of connections.</summary>
    public static IServiceCollection AddConnectionsFeature(this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddSingleton<TokenSealer>();
        services.AddSingleton<JwksKeyCache>();
        services.AddSingleton<ConnectionHandshakeStore>();
        services.AddSingleton<Spotify.ListenAlongDirectory>();
        services.AddSingleton<IConnectionProviderRegistry, ConnectionProviderRegistry>();

        services.AddHttpClient(JwksKeyCache.HttpClientName).AddStandardResilienceHandler(Resilience);

        // The status poller's one call. No retries: a read that failed is re-read on the next tick,
        // and a retried 429 is exactly what the budget exists to prevent.
        services.AddHttpClient<Spotify.SpotifyPlayerClient>(http => http.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient<Twitch.TwitchEventSubClient>(http => http.Timeout = TimeSpan.FromSeconds(15)).AddStandardResilienceHandler(Resilience);

        AddProvider<GitHubConnectionProvider>(services);
        // The Web API key travels in the query string, which the factory's default logger would write
        // out with every request; that logger is dropped for this one client.
        AddProvider<SteamConnectionProvider>(services, quietLogs: true);
        AddProvider<SpotifyConnectionProvider>(services);
        AddProvider<TwitterConnectionProvider>(services);
        AddProvider<TwitchConnectionProvider>(services);
        AddProvider<YouTubeConnectionProvider>(services);
        AddProvider<TelegramConnectionProvider>(services);

        return services;
    }

    private static void AddProvider<T>(IServiceCollection services, bool quietLogs = false) where T : class, IConnectionProvider
    {
        var client = services.AddHttpClient<T>(http => http.Timeout = TimeSpan.FromSeconds(30));

        if (quietLogs)
            client.RemoveAllLoggers();

        client.AddStandardResilienceHandler(Resilience);

        services.AddTransient<IConnectionProvider>(sp => sp.GetRequiredService<T>());
    }

    private static void Resilience(HttpStandardResilienceOptions o)
    {
        o.Retry.MaxRetryAttempts        = 2;
        o.Retry.UseJitter               = true;
        o.AttemptTimeout.Timeout        = TimeSpan.FromSeconds(10);
        o.TotalRequestTimeout.Timeout   = TimeSpan.FromSeconds(25);
    }
}
