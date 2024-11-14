namespace Argon.Api.Features.OrleansStreamingProviders;

using System.Collections.Concurrent;
using System.Diagnostics;
using global::Orleans.Configuration;
using global::Orleans.Providers;
using global::Orleans.Providers.Streams.Common;
using global::Orleans.Providers.Streams.Generator;
using global::Orleans.Serialization;
using global::Orleans.Streams;
using NATS.Client.Core;

public class NatsAdapterFactory : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache, IControllable
{
    private readonly INatsConnection                                               connection;
    private readonly ILogger<NatsAdapterFactory>                                   logger;
    private readonly ILoggerFactory                                                loggerFactory;
    private readonly HashRingStreamQueueMapperOptions                              queueMapperOptions;
    private readonly Serializer                                                    serializer;
    private readonly IServiceProvider                                              serviceProvider;
    private readonly StreamStatisticOptions                                        statisticOptions;
    private          BlockPoolMonitorDimensions                                    blockPoolMonitorDimensions;
    protected        Func<BlockPoolMonitorDimensions, IBlockPoolMonitor>           BlockPoolMonitorFactory;
    private          IObjectPool<FixedSizeBuffer>                                  bufferPool;
    protected        Func<CacheMonitorDimensions, ICacheMonitor>                   CacheMonitorFactory;
    private          IStreamGeneratorConfig?                                       generatorConfig;
    protected        Func<ReceiverMonitorDimensions, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory;
    private          ConcurrentDictionary<QueueId, Receiver>                       receivers;

    public NatsAdapterFactory(string providerName, HashRingStreamQueueMapperOptions queueMapperOptions, StreamStatisticOptions statisticOptions,
        IServiceProvider serviceProvider, Serializer serializer, ILoggerFactory loggerFactory, INatsConnection connection)
    {
        Name                    = providerName;
        this.queueMapperOptions = queueMapperOptions ?? throw new ArgumentNullException(nameof(queueMapperOptions));
        this.statisticOptions   = statisticOptions ?? throw new ArgumentNullException(nameof(statisticOptions));
        this.serviceProvider    = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        this.serializer         = serializer ?? throw new ArgumentNullException(nameof(serializer));
        this.loggerFactory      = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        logger                  = loggerFactory.CreateLogger<NatsAdapterFactory>();
        this.connection         = connection;
        logger.LogInformation("anus");
    }

#region Implementation of IControllable

    public async Task<object> ExecuteCommand(int command, object arg) => throw new NotImplementedException();

#endregion

#region Implementation of IQueueAdapterCache

    public IQueueCache CreateQueueCache(QueueId queueId) => throw new NotImplementedException();

#endregion

    public static NatsAdapterFactory Create(IServiceProvider services, string name)
    {
        var queueMapperOptions = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);
        var statisticOptions   = services.GetOptionsByName<StreamStatisticOptions>(name);
        var factory            = ActivatorUtilities.CreateInstance<NatsAdapterFactory>(services, name, queueMapperOptions, statisticOptions);
        factory.Init();
        return factory;
    }

    private void Init()
    {
        receivers               = new ConcurrentDictionary<QueueId, Receiver>();
        CacheMonitorFactory     = dimensions => new DefaultCacheMonitor(dimensions);
        BlockPoolMonitorFactory = dimensions => new DefaultBlockPoolMonitor(dimensions);
        ReceiverMonitorFactory  = dimensions => new DefaultQueueAdapterReceiverMonitor(dimensions);
        generatorConfig         = serviceProvider.GetKeyedService<IStreamGeneratorConfig>(Name);
        if (generatorConfig == null)
        {
            logger.LogInformation(
                "No generator configuration found for stream provider {StreamProvider}.  Inactive until provided with configuration by command.",
                Name);
        }
    }

    private void SetGeneratorOnReceiver(Receiver receiver)
    {
        if (generatorConfig == null) return;
        var generator = (IStreamGenerator)(serviceProvider.GetService(generatorConfig.StreamGeneratorType) ??
                                           Activator.CreateInstance(generatorConfig.StreamGeneratorType))!;
        if (generator == null) throw new OrleansException($"StreamGenerator type not supported: {generatorConfig.StreamGeneratorType}");
        generator.Configure(serviceProvider, generatorConfig);
        receiver.QueueGenerator = generator;
    }

    private class Receiver(IQueueAdapterReceiverMonitor receiverMonitor) : IQueueAdapterReceiver
    {
        private const int              MaxDelayMs = 20;
        public        IStreamGenerator QueueGenerator { private get; set; }

        public Task Initialize(TimeSpan timeout)
        {
            receiverMonitor?.TrackInitialization(true, TimeSpan.MinValue, null);
            return Task.CompletedTask;
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            var watch = Stopwatch.StartNew();
            await Task.Delay(Random.Shared.Next(1, MaxDelayMs));
            if (!QueueGenerator.TryReadEvents(DateTime.UtcNow, maxCount, out List<IBatchContainer> batches)) return new List<IBatchContainer>();
            watch.Stop();
            receiverMonitor?.TrackRead(true, watch.Elapsed, null);
            if (batches.Count <= 0) return batches;
            var oldestMessage = batches[0] as GeneratedBatchContainer;
            var newestMessage = batches[^1] as GeneratedBatchContainer;
            receiverMonitor?.TrackMessagesReceived(batches.Count, oldestMessage?.EnqueueTimeUtc, newestMessage?.EnqueueTimeUtc);
            return batches;
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages) => Task.CompletedTask;

        public Task Shutdown(TimeSpan timeout)
        {
            receiverMonitor?.TrackShutdown(true, TimeSpan.MinValue, null);
            return Task.CompletedTask;
        }
    }

#region Implementation of IQueueAdapterFactory

    public async Task<IQueueAdapter> CreateAdapter()
    {
        
    }

    public IQueueAdapterCache GetQueueAdapterCache() => throw new NotImplementedException();

    public IStreamQueueMapper GetStreamQueueMapper() => throw new NotImplementedException();

    public async Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) => throw new NotImplementedException();

#endregion

#region Implementation of IQueueAdapter

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token,
        Dictionary<string, object> requestContext) => throw new NotImplementedException();

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        if (!receivers.TryGetValue(queueId, out var receiver))
        {
            var dimensions      = new ReceiverMonitorDimensions(queueId.ToString());
            var receiverMonitor = ReceiverMonitorFactory(dimensions);
            receiver = receivers.GetOrAdd(queueId, new Receiver(receiverMonitor));
        }

        SetGeneratorOnReceiver(receiver);
        return receiver;
    }

    public string                  Name         { get; }
    public bool                    IsRewindable => false;
    public StreamProviderDirection Direction    => StreamProviderDirection.ReadWrite;

#endregion
}