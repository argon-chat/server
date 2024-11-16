namespace Argon.Api.Features.OrleansStreamingProviders;

using System.Collections.Concurrent;
using NATS.Client.Core;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Serialization;
using Orleans.Streams;

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
    private          Func<BlockPoolMonitorDimensions, IBlockPoolMonitor>           BlockPoolMonitorFactory;
    private          IObjectPool<FixedSizeBuffer>?                                 bufferPool;
    private          Func<CacheMonitorDimensions, ICacheMonitor>                   CacheMonitorFactory;
    private          IStreamGeneratorConfig?                                       generatorConfig;
    private          Func<ReceiverMonitorDimensions, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory;
    private          ConcurrentDictionary<QueueId, Receiver>                       receivers;
    private          IStreamFailureHandler?                                        streamFailureHandler;
    private          IStreamQueueMapper?                                           streamQueueMapper;


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

    public async Task<object> ExecuteCommand(int command, object arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        generatorConfig = arg as IStreamGeneratorConfig;
        if (generatorConfig == null) throw new ArgumentOutOfRangeException(nameof(arg), "Arg must by of type IStreamGeneratorConfig");

        foreach (var receiver in receivers) SetGeneratorOnReceiver(receiver.Value);

        return Task.FromResult<object>(true);
    }

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

    internal class NatsResponse : IBatchContainer, IComparable<NatsResponse>
    {
    #region Implementation of IComparable<in NatsResponse>

        public int CompareTo(NatsResponse? other) => throw new NotImplementedException();

    #endregion

    #region Implementation of IBatchContainer

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => throw new NotImplementedException();

        public bool ImportRequestContext() => throw new NotImplementedException();

        public StreamId            StreamId      { get; }
        public StreamSequenceToken SequenceToken { get; }

    #endregion
    }

    private class Receiver(string providerName, IQueueAdapterReceiverMonitor receiverMonitor, INatsConnection connection) : IQueueAdapterReceiver
    {
        private const int              MaxDelayMs = 20;
        public        IStreamGenerator QueueGenerator { private get; set; }

        public Task Initialize(TimeSpan timeout)
        {
            receiverMonitor.TrackInitialization(true, TimeSpan.MinValue, null);
            return Task.CompletedTask;
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            var batches = new List<IBatchContainer>();

            var subscription = connection.SubscribeAsync<string>(providerName);

            await foreach (var natsMsg in subscription)
            {
                var data = natsMsg.Data;
                Console.WriteLine(data);
                batches.Add(new NatsResponse());
                // batches.Add(new BatchContainer(natsMsg));
            }

            return batches;
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages) => Task.CompletedTask;

        public Task Shutdown(TimeSpan timeout)
        {
            receiverMonitor?.TrackShutdown(true, TimeSpan.MinValue, null);
            return Task.CompletedTask;
        }
    }

#region Implementation of IQueueAdapterCache

    public IQueueCache CreateQueueCache(QueueId queueId)
    {
        CreateBufferPoolIfNotCreatedYet();
        var dimensions   = new CacheMonitorDimensions(queueId.ToString(), blockPoolMonitorDimensions.BlockPoolId);
        var cacheMonitor = CacheMonitorFactory(dimensions);
        return new GeneratorPooledCache(bufferPool, loggerFactory.CreateLogger($"{typeof(GeneratorPooledCache).FullName}.{Name}.{queueId}"),
            serializer, cacheMonitor, statisticOptions.StatisticMonitorWriteInterval);
    }

    private void CreateBufferPoolIfNotCreatedYet()
    {
        if (bufferPool != null) return;
        blockPoolMonitorDimensions = new BlockPoolMonitorDimensions($"BlockPool-{Guid.NewGuid()}");
        var oneMb             = 1 << 20;
        var objectPoolMonitor = new ObjectPoolMonitorBridge(BlockPoolMonitorFactory(blockPoolMonitorDimensions), oneMb);
        bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(oneMb), objectPoolMonitor,
            statisticOptions.StatisticMonitorWriteInterval);
    }

#endregion

#region Implementation of IQueueAdapterFactory

    public Task<IQueueAdapter> CreateAdapter()        => Task.FromResult<IQueueAdapter>(this);
    public IQueueAdapterCache  GetQueueAdapterCache() => this;

    public IStreamQueueMapper GetStreamQueueMapper() =>
        streamQueueMapper ??= new HashRingBasedStreamQueueMapper(queueMapperOptions, Name);


    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) =>
        Task.FromResult(streamFailureHandler ??= new NoOpStreamDeliveryFailureHandler());

#endregion

#region Implementation of IQueueAdapter

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token,
        Dictionary<string, object> requestContext)
    {
        foreach (var eventData in events)
        {
            if (eventData is null) continue;
            // var subject = $"{Name}/{streamId}";
            var json = JsonConvert.SerializeObject(eventData);
            await connection.PublishAsync(Name, json);
        }
    }

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        if (receivers.TryGetValue(queueId, out var receiver)) return receiver;
        var dimensions      = new ReceiverMonitorDimensions(queueId.ToString());
        var receiverMonitor = ReceiverMonitorFactory(dimensions);
        receiver = receivers.GetOrAdd(queueId, new Receiver(Name, receiverMonitor, connection));

        return receiver;
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


    public string                  Name         { get; }
    public bool                    IsRewindable => false;
    public StreamProviderDirection Direction    => StreamProviderDirection.ReadWrite;

#endregion
}