namespace Argon.Features.NatsStreaming;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Orleans;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

public class NatsQueueAdapterFactory(
    string name,
    ILogger<NatsQueueAdapterFactory> logger,
    IServiceProvider serviceProvider)
    : IQueueAdapterFactory, IQueueAdapterCache
{
    private Lazy<HashRingBasedStreamQueueMapper> _mapper
        => new(() => new(new HashRingStreamQueueMapperOptions(), name));
    private Lazy<INatsMessageBodySerializer> Serializer 
        => new(() => serviceProvider.GetRequiredKeyedService<INatsMessageBodySerializer>(name));

    public string Name { get; set; } = name;

    public async Task<IQueueAdapter> CreateAdapter()
    {
        try
        {
            var natsOptions = NatsOpts.Default with
            {
                ConnectTimeout = TimeSpan.FromMinutes(2),
                CommandTimeout = TimeSpan.FromMinutes(2),
                RequestTimeout = TimeSpan.FromMinutes(2)
            };
            natsOptions = serviceProvider.GetOptionsByName<NatsConfiguration>(name).Configure(natsOptions);
            var connection = new NatsConnection(natsOptions);
            var context    = new NatsJSContext(connection);
            return new NatsAdaptor(context, Name, Serializer.Value, _mapper.Value, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create adapter {Exception}", ex);
            throw;
        }
    }

    public IQueueAdapterCache GetQueueAdapterCache()
        => this;

    public IStreamQueueMapper GetStreamQueueMapper()
        => _mapper.Value;

    public async Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        => new NoOpStreamDeliveryFailureHandler();

    public IQueueCache CreateQueueCache(QueueId queueId)
        => new SimpleQueueCache(1024 * 4, NullLogger.Instance);

    public static NatsQueueAdapterFactory Create(IServiceProvider services, string name)
    {
        var queueMapperOptions = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);
        var natsOpts = services.GetOptionsByName<NatsConfiguration>(name);
        var serializer = services.GetRequiredKeyedService<INatsMessageBodySerializer>(name);
        var factory = ActivatorUtilities.CreateInstance<NatsQueueAdapterFactory>(services, name, queueMapperOptions, natsOpts, serializer);
        return factory;
    }
}