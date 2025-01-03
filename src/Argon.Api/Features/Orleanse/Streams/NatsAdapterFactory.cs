namespace Argon.Features.OrleansStreamingProviders;

using NATS.Client.Core;
using NATS.Client.JetStream;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

[Serializable, GenerateSerializer, Alias(nameof(ArgonEventBatch))]
public class ArgonEventBatch : IBatchContainer
{
#region Implementation of IBatchContainer

    public ArgonEventBatch()
    {
    }

    public ArgonEventBatch(StreamId streamId, object data, Type getType, StreamSequenceToken? eventToken, NatsJSMsg<string> msg)
    {
        dataType      = getType;
        StreamId      = streamId;
        Data          = [data];
        SequenceToken = eventToken;
        Event         = msg;
    }

    [Id(0)]
    private List<object> Data { get; }
    private Type              dataType { get; }
    public  NatsJSMsg<string> Event    { get; }
    [Id(1)]
    public StreamId StreamId { get; }
    public StreamSequenceToken SequenceToken { get; }

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
    {
        if (SequenceToken is null)
            throw new Exception($"SequenceToken is null");

        if (SequenceToken is not EventSequenceTokenV2 sequenceTokenV2)
            throw new Exception($"SequenceToken is not EventSequenceTokenV2, {SequenceToken.GetType()}");

        return Data.OfType<T>().ToList().Select((@event, i) => Tuple.Create<T, StreamSequenceToken>(@event, sequenceTokenV2.CreateSequenceTokenForEvent(i)));
    }

    public bool ImportRequestContext() => false;

#endregion
}

public class NatsAdapterFactory : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache
{
#region Implementation of IQueueAdapterCache

    public IQueueCache CreateQueueCache(QueueId queueId) => _adapterCache.CreateQueueCache(queueId);

#endregion

    public static NatsAdapterFactory Create(IServiceProvider services, string name) =>
        ActivatorUtilities.CreateInstance<NatsAdapterFactory>(services, name);

    private class Receiver(
        string providerName,
        IQueueAdapterReceiverMonitor receiverMonitor,
        OrleansJsonSerializer serializationManager,
        ILogger<Receiver> logger,
        QueueId queueId,
        INatsConnection connection,
        INatsJSContext js,
        INatsJSStream stream,
        INatsJSConsumer consumer) : IQueueAdapterReceiver
    {
        public Task Initialize(TimeSpan timeout)
        {
            receiverMonitor.TrackInitialization(true, TimeSpan.MinValue, null);
            return Task.CompletedTask;
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount) =>
            await consumer.FetchAsync<string>(new NatsJSFetchOpts
            {
                MaxMsgs = 1, // TODO: for later optimizations change this number
                Expires = TimeSpan.FromSeconds(1)
            }).Select(natsMsg => ToBatch(natsMsg, serializationManager)).Select(IBatchContainer (dummy) => dummy).ToListAsync();

        private ArgonEventBatch ToBatch(NatsJSMsg<string> msg, OrleansJsonSerializer serializationManager)
        {
            if (msg.Headers is null)
                throw new NullReferenceException($"msg.Headers is null");
            if (!msg.Headers.TryGetValue("streamId", out var stream))
                throw new KeyNotFoundException($"streamId suka not found");
            if (!msg.Headers.TryGetValue("eventInd", out var eventIndexStr))
                throw new KeyNotFoundException($"eventInd suka not found");
            if (!msg.Headers.TryGetValue("seq", out var seqStr))
                throw new KeyNotFoundException($"seq suka not found");
            if (!int.TryParse(eventIndexStr, out var eventIndex))
                throw new Exception($"eventIndex failed to parse suka '{eventIndexStr}'");
            if (!int.TryParse(seqStr, out var seq))
                throw new Exception($"seq failed to parse suka '{seqStr}'");


            var streamId = StreamId.Parse(Encoding.UTF8.GetBytes(stream));
            var data     = serializationManager.Deserialize(typeof(object), msg.Data);
            return new ArgonEventBatch(streamId, data, data.GetType(), new EventSequenceTokenV2(eventIndex, seq), msg);
        }

        public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            foreach (var iBatchContainer in messages)
            {
                var argonEvent = (ArgonEventBatch)iBatchContainer;
                await argonEvent.Event.AckAsync();
            }
        }

        public Task Shutdown(TimeSpan timeout)
        {
            receiverMonitor.TrackShutdown(true, TimeSpan.MinValue, null);
            return Task.CompletedTask;
        }
    }

#region Implementation of IQueueAdapterFactory

    private readonly OrleansJsonSerializer                                         _serializationManager;
    private readonly ILoggerFactory                                                _loggerFactory;
    private readonly IGrainFactory                                                 _grainFactory;
    private readonly IQueueAdapterCache                                            _adapterCache;
    private readonly HashRingStreamQueueMapperOptions                              _queueMapperOptions;
    private readonly IStreamQueueMapper                                            _streamQueueMapper;
    private readonly ILogger<NatsAdapterFactory>                                   _logger;
    private readonly INatsConnection                                               _connection;
    private readonly ConcurrentDictionary<QueueId, Receiver>                       _receivers;
    private readonly Func<ReceiverMonitorDimensions, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory;
    private readonly INatsJSContext                                                _js;
    private readonly AsyncContainer<INatsJSStream>                                 _stream;
    private readonly AsyncContainer<INatsJSConsumer>                               _consumer;


    public NatsAdapterFactory(string name, OrleansJsonSerializer serializationManager, ILoggerFactory loggerFactory, IGrainFactory grainFactory,
        IServiceProvider services, INatsJSContext js, AsyncContainer<INatsJSStream> stream, AsyncContainer<INatsJSConsumer> consumer)
    {
        Name                   = name;
        _serializationManager  = serializationManager;
        _loggerFactory         = loggerFactory;
        _grainFactory          = grainFactory;
        _queueMapperOptions    = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);
        _logger                = loggerFactory.CreateLogger<NatsAdapterFactory>();
        _adapterCache          = new SimpleQueueAdapterCache(new SimpleQueueCacheOptions(), name, loggerFactory);
        _connection            = services.GetRequiredService<INatsConnection>();
        _receivers             = new ConcurrentDictionary<QueueId, Receiver>();
        ReceiverMonitorFactory = dimensions => new DefaultQueueAdapterReceiverMonitor(dimensions);
        _streamQueueMapper     = new HashRingBasedStreamQueueMapper(_queueMapperOptions, Name);
        _js                    = js;
        _stream                = stream;
        _consumer              = consumer;
        _logger.LogInformation("Initializing NATS");
    }

    public async Task<IQueueAdapter> CreateAdapter()
    {
        await this._stream.DoCreateAsync();
        await this._consumer.DoCreateAsync();
        return this;
    }

    public IQueueAdapterCache GetQueueAdapterCache() => this;

    public IStreamQueueMapper GetStreamQueueMapper() => _streamQueueMapper;

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) =>
        Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler(false));

#endregion

#region Implementation of IQueueAdapter

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token,
        Dictionary<string, object> requestContext)
    {
        // _stream = await _js.CreateStreamAsync(new StreamConfig($"{streamId.GetNamespace()}{streamId.GetKeyAsString()}", ["*"]));

        var headers = new NatsHeaders
        {
            {
                "streamId", streamId.ToString()
            },
            {
                "eventInd", token?.EventIndex.ToString() ?? "0"
            },
            {
                "seq", token?.SequenceNumber.ToString() ?? "0"
            }
        };
        var name = Name.Replace(".", "_");
        foreach (var eventData in events)
            await _js.PublishAsync($"argon.streams.{name}", _serializationManager.Serialize(eventData, eventData.GetType()), headers: headers);
        // await _js.PublishConcurrentAsync(Name, _serializationManager.Serialize(eventData, eventData.GetType()), headers: headers);
    }

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        if (_receivers.TryGetValue(queueId, out var receiver)) return receiver;
        var dimensions      = new ReceiverMonitorDimensions(queueId.ToString());
        var receiverMonitor = ReceiverMonitorFactory(dimensions);
        receiver = _receivers.GetOrAdd(queueId,
            new Receiver(Name, receiverMonitor, _serializationManager, _loggerFactory.CreateLogger<Receiver>(), queueId, _connection, _js,
                _stream.Value,
                _consumer.Value));

        return receiver;
    }

    public string                  Name         { get; }
    public bool                    IsRewindable => false;
    public StreamProviderDirection Direction    => StreamProviderDirection.ReadWrite;

#endregion
}