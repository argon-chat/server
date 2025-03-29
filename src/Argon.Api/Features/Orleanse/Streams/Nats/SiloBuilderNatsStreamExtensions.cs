namespace Argon.Features.NatsStreaming;

using Orleans.Streams;

public static class SiloBuilderNatsStreamExtensions
{
    public static ISiloBuilder AddNatsStreams(this ISiloBuilder builder, string name,
        Action<ISiloMemoryStreamConfigurator>? configure = null)
        => AddNatsStreams<DefaultNatsMessageBodySerializer>(builder, name, configure);

    public static ISiloBuilder AddNatsStreams<TSerializer>(this ISiloBuilder builder, string name,
        Action<ISiloMemoryStreamConfigurator>? configure = null)
        where TSerializer : class, INatsMessageBodySerializer
    {
        var natsStreamConfigurator = new SiloNatsStreamConfigurator<TSerializer>(name,
            configureDelegate => builder.ConfigureServices(configureDelegate)
        );
        configure?.Invoke(natsStreamConfigurator);
        return builder;
    }

    public static IClientBuilder AddNatsStreams<TSerializer>(
        this IClientBuilder builder,
        string name,
        Action<IClusterClientPersistentStreamConfigurator>? configure = null)
        where TSerializer : class, INatsMessageBodySerializer
    {
        var configurator = new ClusterClientNatsStreamConfigurator<TSerializer>(name, builder);
        configure?.Invoke(configurator);
        return builder;
    }

    public static IClientBuilder AddNatsStreams(
        this IClientBuilder builder,
        string name,
        Action<IClusterClientPersistentStreamConfigurator>? configure = null)
        => builder.AddNatsStreams<DefaultNatsMessageBodySerializer>(name, configure);
}