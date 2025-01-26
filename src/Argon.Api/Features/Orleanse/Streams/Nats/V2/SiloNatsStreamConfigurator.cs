namespace Argon.Features.OrleansStreamingProviders.V2;

using NATS.Client;
using NATS.Client.JetStream;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;
using Serializer = Orleans.Serialization.Serializer;

public class SiloNatsStreamConfigurator : SiloPersistentStreamConfigurator
{
    public SiloNatsStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate)
        : base(name, configureServicesDelegate, NatsQueueAdapterFactory.Create)
    {
        ConfigureDelegate(services =>
        {
            services.ConfigureNamedOptionForLogging<NatsOptions>(name)
               .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
               .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
        });
    }

    public SiloNatsStreamConfigurator ConfigureNats(Action<OptionsBuilder<NatsOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;
    }

    public SiloNatsStreamConfigurator ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
    {
        this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
        return this;
    }

    public SiloNatsStreamConfigurator ConfigurePartitioning(int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfparitions));
        return this;
    }
}

public class ClusterClientNatsStreamConfigurator : ClusterClientPersistentStreamConfigurator
{
    public ClusterClientNatsStreamConfigurator(string name, IClientBuilder builder)
        : base(name, builder, NatsQueueAdapterFactory.Create)
        => builder.ConfigureServices(services =>
        {
            services.ConfigureNamedOptionForLogging<NatsOptions>(name)
               .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
        });

    public ClusterClientNatsStreamConfigurator ConfigureNats(Action<OptionsBuilder<NatsOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;
    }

    public ClusterClientNatsStreamConfigurator ConfigurePartitioning(int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfparitions));
        return this;
    }
}

public class NatsQueueAdapterReceiver(
    Serializer<NatsBatchContainer> serializationManager,
    IJetStream jetStream,
    string stream,
    ILogger<IQueueAdapterReceiver> logger)
    : IQueueAdapterReceiver
{
    private TimeSpan _timeout = TimeSpan.FromSeconds(1);

    private long _lastReadMessage;

    private IJetStreamPullSubscription _subscription;

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        const int MaxNumberOfMessagesToPeek = 256;

        var count = maxCount < 0 ? MaxNumberOfMessagesToPeek : Math.Min(maxCount, MaxNumberOfMessagesToPeek);

        logger.LogWarning("GetQueueMessagesAsync called, {maxCount}, {count}", maxCount, count);

        if (_subscription is null)
        {
            logger.LogWarning("IJetStreamPullSubscription NOT inited in GetQueueMessagesAsync");
            await Initialize(TimeSpan.FromMinutes(15));
            return new List<IBatchContainer>();
        }

        var fetched = _subscription.Fetch(count, (int)_timeout.TotalMilliseconds);

        IList<IBatchContainer> result = fetched
           .Select(message => NatsBatchContainer.FromNatsMessage(serializationManager, message, _lastReadMessage++)).Cast<IBatchContainer>()
           .ToList();

        return result;
    }

    public Task Initialize(TimeSpan timeout)
    {
        logger.LogWarning("IQueueAdapterReceiver called Initialize");

        var cc = Nats.GetConsumer(stream);
        var options = PullSubscribeOptions
           .Builder()
           .WithConfiguration(cc)
           .Build();

        _timeout      = timeout;
        _subscription = jetStream.PullSubscribe($"{stream}.request", options);

        return Task.CompletedTask;
    }

    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        foreach (var message in messages.OfType<NatsBatchContainer>())
        {
            if (message.Message == null)
                continue;
            message.Message.Ack();
            message.Message = null;
        }

        return Task.CompletedTask;
    }

    public Task Shutdown(TimeSpan timeout)
    {
        if (_subscription != null)
            _subscription.Dispose();

        return Task.CompletedTask;
    }
}

public class NatsQueueAdapterFactory : IQueueAdapterFactory
{
    private readonly string _name;

    private readonly IJetStream _jetStream;

    private readonly ILoggerFactory _loggerFactory;

    private readonly IQueueAdapterCache _adapterCache;

    private readonly IServiceProvider _serviceProvider;

    private readonly Serializer _serializer;

    private readonly SimpleQueueCacheOptions _cacheOptions;

    private readonly IOptions<ClusterOptions> _clusterOptions;

    private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;

    public NatsQueueAdapterFactory(string name,
        IJetStream jetStream,
        HashRingStreamQueueMapperOptions queueMapperOptions,
        SimpleQueueCacheOptions cacheOptions,
        IServiceProvider serviceProvider,
        IOptions<ClusterOptions> clusterOptions,
        Serializer serializer,
        ILoggerFactory loggerFactory)
    {
        _name              = name;
        _jetStream         = jetStream;
        _serializer        = serializer;
        _cacheOptions      = cacheOptions;
        _loggerFactory     = loggerFactory;
        _clusterOptions    = clusterOptions;
        _serviceProvider   = serviceProvider;
        _streamQueueMapper = new HashRingBasedStreamQueueMapper(queueMapperOptions, _name);
        _adapterCache      = new SimpleQueueAdapterCache(cacheOptions, _name, _loggerFactory);
    }

    public static NatsQueueAdapterFactory Create(IServiceProvider services, string name)
    {
        var clusterOptions     = services.GetProviderClusterOptions(name);
        var natsOptions        = services.GetOptionsByName<NatsOptions>(name);
        var cacheOptions       = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
        var queueMapperOptions = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);

        var cf        = new ConnectionFactory();
        var qr        = cf.CreateConnection(natsOptions.ConnectionString);
        var jetStream = qr.CreateJetStreamContext();

        return ActivatorUtilities.CreateInstance<NatsQueueAdapterFactory>(services, name, jetStream, queueMapperOptions, cacheOptions, services,
            clusterOptions);
    }

    public Task<IQueueAdapter> CreateAdapter()
        => Task.FromResult<IQueueAdapter>(new NatsQueueAdapter(_serializer, _streamQueueMapper, _loggerFactory, _jetStream, _name));

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());

    public IQueueAdapterCache GetQueueAdapterCache()
        => _adapterCache;

    public IStreamQueueMapper GetStreamQueueMapper()
        => _streamQueueMapper;
}

public class NatsQueueAdapter(
    Serializer serializer,
    IConsistentRingStreamQueueMapper streamQueueMapper,
    ILoggerFactory loggerFactory,
    IJetStream jetStream,
    string providerName)
    : IQueueAdapter
{
    private readonly Serializer<NatsBatchContainer> _serializer = serializer.GetSerializer<NatsBatchContainer>();

    public bool IsRewindable => false;

    public string Name { get; } = providerName;

    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        var rec = new NatsQueueAdapterReceiver(_serializer, jetStream, queueId.ToString(), loggerFactory.CreateLogger<IQueueAdapterReceiver>());

        //rec.Initialize(TimeSpan.FromMinutes(1)).Wait();

        return rec;
    }

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token,
        Dictionary<string, object> requestContext)
    {
        var queueId = streamQueueMapper.GetQueueForStream(streamId);
        var message = NatsBatchContainer.ToMessage(_serializer, streamId, events, requestContext);
        var builder = PublishOptions.Builder()
           .WithTimeout(5000)
           .WithStream(queueId.ToString())
           .WithMessageId(Guid.NewGuid().ToString());

        var ack = await jetStream.PublishAsync($"{queueId}.request", message.Data, builder.Build());
    }
}

public class NatsOptions
{
    [Redact]
    public string ConnectionString { get; set; }
}

[Serializable]
[GenerateSerializer]
public class NatsBatchContainer : IBatchContainer
{
    /// <summary>
    /// Need to store reference to the original Message to be able to delete it later on.
    /// </summary>
    [NonSerialized]
    public Msg Message;

    [JsonProperty]
    [Id(1)]
    private readonly List<object> _events;

    [JsonProperty]
    [Id(2)]
    private readonly Dictionary<string, object> _requestContext;

    [JsonProperty]
    [Id(0)]
    private EventSequenceTokenV2 _sequenceToken;

    [JsonConstructor]
    private NatsBatchContainer(StreamId streamId,
        List<object> events,
        Dictionary<string, object> requestContext,
        EventSequenceTokenV2 sequenceToken)
        : this(streamId, events, requestContext)
    {
        _sequenceToken = sequenceToken;
    }

    private NatsBatchContainer(StreamId streamId,
        List<object> events,
        Dictionary<string, object> requestContext)
    {
        StreamId = streamId;

        _requestContext = requestContext;
        _events         = events ?? throw new ArgumentNullException(nameof(events), "Message contains no events");
    }

    [Id(3)]
    public StreamId StreamId { get; }

    public StreamSequenceToken SequenceToken => _sequenceToken;

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
    {
        return _events.OfType<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, _sequenceToken.CreateSequenceTokenForEvent(i)));
    }

    public bool ImportRequestContext()
    {
        if (_requestContext != null)
        {
            RequestContextExtensions.Import(_requestContext);
            return true;
        }

        return false;
    }

    public override string ToString()
    {
        return string.Format($"[{nameof(NatsBatchContainer)}:Stream={0},#Items={1}]", StreamId, _events.Count);
    }

    internal static Msg ToMessage<T>(Serializer<NatsBatchContainer> serializer, StreamId streamId, IEnumerable<T> events,
        Dictionary<string, object> requestContext)
    {
        var batchMessage = new NatsBatchContainer(streamId, events.Cast<object>().ToList(), requestContext);
        var rawBytes     = serializer.SerializeToArray(batchMessage);
        var payload = new JObject
        {
            {
                "payload", JToken.FromObject(rawBytes)
            }
        };

        return new Msg(Encoding.Default.GetString(streamId.Namespace.ToArray()), Encoding.Default.GetBytes(payload.ToString()));
    }

    internal static NatsBatchContainer FromNatsMessage(Serializer<NatsBatchContainer> serializer, Msg msg, long sequenceId)
    {
        var json    = JObject.Parse(Encoding.Default.GetString(msg.Data));
        var payload = json["payload"];

        if (payload != null)
        {
            var data  = payload.ToObject<byte[]>();
            var batch = serializer.Deserialize(data);

            batch.Message        = msg;
            batch._sequenceToken = new EventSequenceTokenV2(sequenceId);

            return batch;
        }

        throw new InvalidOperationException("Payload is null");
    }
}

/// <summary>
/// Utility class for generating NATS configuration.
/// </summary>
public static class Nats
{
    /// <summary>
    /// Generate Stream configuration by name.
    /// </summary>
    /// <param name="stream">Name of the stream.</param>
    /// <param name="storageType">Keep it in file or memory.</param>
    /// <returns>Pregenerated stream configuration.</returns>
    public static StreamConfiguration GetStream(string stream, StorageType storageType)
    {
        return StreamConfiguration.Builder()
           .WithName(stream)
           .WithStorageType(storageType)
           .WithRetentionPolicy(NATS.Client.JetStream.RetentionPolicy.WorkQueue)
           .WithSubjects($"{stream}.*")
           .Build();
    }

    /// <summary>
    /// Generate Consumer configuration by name.
    /// </summary>
    /// <param name="stream">Name of the stream to consume.</param>
    /// <returns>Pregenerated consumer configuration.</returns>
    public static ConsumerConfiguration GetConsumer(string stream)
    {
        return ConsumerConfiguration.Builder()
           .WithDurable($"{stream}")
           .WithFilterSubject($"{stream}.request")
           .Build();
    }

    /// <summary>
    /// Create a stream in NATS.
    /// </summary>
    /// <param name="management">JetStream management context.</param>
    /// <param name="stream">Stream name.</param>
    /// <param name="storageType">Stream storage type.</param>
    public static void Prepare(IJetStreamManagement management, string stream, StorageType storageType)
    {
        StreamInfo streamInfo = null;

        try
        {
            streamInfo = management.GetStreamInfo(stream);
        }
        catch (NATSJetStreamException ex)
        {
            if (ex.ErrorCode != 404)
            {
                throw;
            }
        }

        if (streamInfo == null)
        {
            var sc = GetStream(stream, storageType);

            management.AddStream(sc);
        }

        ConsumerInfo consumerInfo = null;

        if (streamInfo != null)
        {
            try
            {
                consumerInfo = management.GetConsumerInfo(stream, $"{stream}");
            }
            catch (NATSJetStreamException ex)
            {
                if (ex.ErrorCode != 404)
                {
                    throw;
                }
            }
        }

        if (consumerInfo == null)
        {
            var cc = GetConsumer(stream);

            management.AddOrUpdateConsumer(stream, cc);
        }
    }

    /// <summary>
    /// Delete a stream in NATS.
    /// </summary>
    /// <param name="management">JetStream management context.</param>
    /// <param name="stream">Stream name.</param>
    public static void Delete(IJetStreamManagement management, string stream)
    {
        StreamInfo streamInfo = null;

        try
        {
            streamInfo = management.GetStreamInfo(stream);
        }
        catch (NATSJetStreamException ex)
        {
            if (ex.ErrorCode != 404)
            {
                throw;
            }
        }

        if (streamInfo != null)
        {
            management.DeleteStream(stream);
        }
    }
}

public static class SiloBuilderExtensions
{
    public static ISiloBuilder AddNatsStreams(this ISiloBuilder builder, string name, Action<NatsOptions> configureOptions)
    {
        builder.AddNatsStreams(name, b => b.ConfigureNats(ob => ob.Configure(configureOptions)));
        return builder;
    }

    private static ISiloBuilder AddNatsStreams(this ISiloBuilder builder, string name, Action<SiloNatsStreamConfigurator> configure)
    {
        var configurator = new SiloNatsStreamConfigurator(name, configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
        configure?.Invoke(configurator);
        return builder;
    }
}

public static class ClientBuilderExtensions
{
    public static IClientBuilder AddNatsStreams(this IClientBuilder builder, string name, Action<NatsOptions> configureOptions)
    {
        builder.AddNatsStreams(name, b => b.ConfigureNats(ob => ob.Configure(configureOptions)));
        return builder;
    }

    private static IClientBuilder AddNatsStreams(this IClientBuilder builder, string name, Action<ClusterClientNatsStreamConfigurator> configure)
    {
        var configurator = new ClusterClientNatsStreamConfigurator(name, builder);
        configure?.Invoke(configurator);
        return builder;
    }
}