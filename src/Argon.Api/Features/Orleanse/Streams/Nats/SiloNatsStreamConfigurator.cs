namespace Argon.Features.NatsStreaming;

using System.Buffers;
using Orleans.Configuration;
using Orleans.Providers;

public class SiloNatsStreamConfigurator<TSerializer> : SiloRecoverableStreamConfigurator, ISiloMemoryStreamConfigurator
    where TSerializer : class, INatsMessageBodySerializer
{
    public SiloNatsStreamConfigurator(
        string name, Action<Action<IServiceCollection>> configureServicesDelegate)
        : base(name, configureServicesDelegate, NatsQueueAdapterFactory.Create)
        => ConfigureDelegate(services => {
            services.AddKeyedTransient<INatsMessageBodySerializer, TSerializer>(name);
            services.ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
        });
}

public class ClusterClientNatsStreamConfigurator<TSerializer> : ClusterClientPersistentStreamConfigurator
    where TSerializer : class, INatsMessageBodySerializer
{
    public ClusterClientNatsStreamConfigurator(string name, IClientBuilder builder)
        : base(name, builder, NatsQueueAdapterFactory.Create)
        => builder.ConfigureServices(services => {
            services.AddKeyedTransient<INatsMessageBodySerializer, TSerializer>(name);
            services.ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name);
            services.ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
            services.Configure<HashRingStreamQueueMapperOptions>(name, options => {
                options.TotalQueueCount = 8;
            });
        });
}
public interface INatsMessageBodySerializer
{
    public void       Serialize(MemoryMessageBody body, IBufferWriter<byte> bufferWriter);
    MemoryMessageBody Deserialize(ReadOnlySequence<byte> bodyBytes);
}
//public class SiloNatsStreamConfigurator<TSerializer> : SiloRecoverableStreamConfigurator, ISiloMemoryStreamConfigurator
//    where TSerializer : class, INatsMessageBodySerializer
//{
//    public SiloNatsStreamConfigurator(
//        string name, Action<Action<IServiceCollection>> configureServicesDelegate)
//        : base(name, configureServicesDelegate, (sp, streamName) =>
//            sp.GetRequiredKeyedService<IQueueAdapterFactory>(streamName))
//        => ConfigureDelegate(services =>
//        {
//            services.AddKeyedTransient<INatsMessageBodySerializer, TSerializer>(name);

//            services.Configure<HashRingStreamQueueMapperOptions>(name, options => { options.TotalQueueCount = 8; });

//            services.ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);

//            services.AddKeyedSingleton<IQueueAdapterFactory, NatsQueueAdapterFactory>(name,
//                (provider, key) => ActivatorUtilities.CreateInstance<NatsQueueAdapterFactory>(provider, key as string));
//        });
//}

//public interface INatsMessageBodySerializer
//{
//    public void       Serialize(MemoryMessageBody body, IBufferWriter<byte> bufferWriter);
//    MemoryMessageBody Deserialize(ReadOnlySequence<byte> bodyBytes);
//}