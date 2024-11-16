namespace Argon.Api.Features.OrleansStreamingProviders;

using System.Collections.Concurrent;
using NATS.Client.Core;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Serialization;
using Orleans.Streams;

public static class NatsMsgExtension
{
    public static ArgonEventBatch ToBatch(this NatsMsg<string> msg, OrleansJsonSerializer serializationManager)
    {
        var                  data       = serializationManager.Deserialize(typeof(object), msg.Data);
        var                  stream     = msg.Headers?["streamId"][0];
        var                  streamId   = StreamId.Parse(Encoding.UTF8.GetBytes(stream));
        var                  eventIndex = int.TryParse(msg.Headers?["eventInd"][0], out var index);
        var                  seq        = int.TryParse(msg.Headers?["seq"][0], out var sequence);
        StreamSequenceToken? token      = null;
        if (eventIndex && seq) token    = new EventSequenceTokenV2(index, sequence);
        return new ArgonEventBatch(streamId, data, data.GetType(), token);
    }
}

public class ArgonEventBatch : IBatchContainer
{
#region Implementation of IBatchContainer

    public ArgonEventBatch() { }

    public ArgonEventBatch(StreamId streamId, object data, Type getType, StreamSequenceToken? eventToken)
    {
        dataType      = getType;
        StreamId      = streamId;
        Data          = [data];
        SequenceToken = eventToken;
    }

    private List<object> Data     { get; }
    private Type         dataType { get; }

    public StreamId            StreamId      { get; }
    public StreamSequenceToken SequenceToken { get; }

    public IEnumerable<Tuple<T, StreamSequenceToken?>> GetEvents<T>()
    {
        var sequenceToken = (EventSequenceTokenV2)SequenceToken;

        return Data.OfType<T>().Select((@event, i) => Tuple.Create<T, StreamSequenceToken?>(@event, sequenceToken.CreateSequenceTokenForEvent(i)));
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
        INatsConnection connection) : IQueueAdapterReceiver
    {
        private const int              MaxDelayMs = 20;
        public        IStreamGenerator QueueGenerator { private get; set; }

        public Task Initialize(TimeSpan timeout)
        {
            receiverMonitor.TrackInitialization(true, TimeSpan.MinValue, null);
            return Task.CompletedTask;
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount) => await connection.SubscribeAsync<string>(providerName)
           .Select(natsMsg => natsMsg.ToBatch(serializationManager)).Select(IBatchContainer (batch) => batch).ToListAsync();

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages) => Task.CompletedTask;

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


    public NatsAdapterFactory(string name, OrleansJsonSerializer serializationManager, ILoggerFactory loggerFactory, IGrainFactory grainFactory,
        IServiceProvider services)
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
        _logger.LogInformation("Initializing NATS");
    }

    public Task<IQueueAdapter> CreateAdapter() => Task.FromResult<IQueueAdapter>(this);

    public IQueueAdapterCache GetQueueAdapterCache() => this;

    public IStreamQueueMapper GetStreamQueueMapper() => _streamQueueMapper;

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) =>
        Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler(false));

#endregion

#region Implementation of IQueueAdapter

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token,
        Dictionary<string, object> requestContext)
    {
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
        foreach (var eventData in events)
            await _connection.PublishAsync(Name, _serializationManager.Serialize(eventData, eventData.GetType()), headers);
    }

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        if (_receivers.TryGetValue(queueId, out var receiver)) return receiver;
        var dimensions      = new ReceiverMonitorDimensions(queueId.ToString());
        var receiverMonitor = ReceiverMonitorFactory(dimensions);
        receiver = _receivers.GetOrAdd(queueId,
            new Receiver(Name, receiverMonitor, _serializationManager, _loggerFactory.CreateLogger<Receiver>(), queueId, _connection));

        return receiver;
    }

    public string                  Name         { get; }
    public bool                    IsRewindable => false;
    public StreamProviderDirection Direction    => StreamProviderDirection.ReadWrite;

#endregion
}