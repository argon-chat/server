namespace Argon.Features.Middlewares;

using Microsoft.Extensions.DependencyInjection.Extensions;

public static class ServerTimingExtensions
{
    public static IServiceCollection AddServerTiming(this IServiceCollection services)
    {
        services.TryAdd(ServiceDescriptor.Scoped<IServerTimingRecorder, ServerTimingRecorder>());
        return services;
    }

    public static IServiceCollection AddServerTiming(
        this IServiceCollection services,
        Action<ServerTimingOptions> options)
    {
        services.Configure(options);
        services.AddServerTiming();

        return services;
    }

    public static IApplicationBuilder UseServerTiming(this IApplicationBuilder builder)
        => builder.UseMiddleware<ServerTimingMiddleware>();
}