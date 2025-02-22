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
        => ConfigureDelegate(services =>
        {
            services.AddKeyedTransient<INatsMessageBodySerializer, TSerializer>(name);
            services.ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
        });
}

public interface INatsMessageBodySerializer
{
    public void Serialize(MemoryMessageBody body, IBufferWriter<byte> bufferWriter);
    MemoryMessageBody Deserialize(ReadOnlySequence<byte> bodyBytes);
}