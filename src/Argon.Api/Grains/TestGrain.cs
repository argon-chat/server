namespace Argon.Api.Grains;

using Contracts;
using Interfaces;
using Orleans.Streams;

#if DEBUG

public class TestGrain([PersistentState("input", "RedisStorage")] IPersistentState<SomeInput> inputStore) : Grain, ITestGrain
{
#region Implementation of ITestGrain

    public async Task<SomeInput> CreateSomeInput(SomeInput input)
    {
        inputStore.State = input;
        await inputStore.WriteStateAsync();
        return inputStore.State;
    }

    public async Task<SomeInput> UpdateSomeInput(SomeInput input)
    {
        inputStore.State = input;
        await inputStore.WriteStateAsync();
        return inputStore.State;
    }

    public async Task<SomeInput> DeleteSomeInput()
    {
        var obj = inputStore.State;
        await inputStore.ClearStateAsync();
        return obj;
    }

    public async Task<SomeInput> GetSomeInput()
    {
        await inputStore.ReadStateAsync();
        return inputStore.State;
    }

#endregion
}

public class StreamProducerGrain : Grain, IStreamProducerGrain
{
#region Implementation of IStreamProducerGrain

    [Obsolete("Obsolete")]
    public Task Produce()
    {
        // Pick a GUID for a chat room grain and chat room stream
        var guid           = Guid.Parse("d97e7fb1-e2f6-4803-b66c-965bc5d1d099");
        var streamProvider = this.GetStreamProvider(IArgonEvent.ProviderId);
        var streamId       = StreamId.Create(IArgonEvent.Namespace, guid);
        var stream         = streamProvider.GetStream<long>(streamId);

        RegisterTimer(_ =>
        {
            var unixEpochNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return stream.OnNextAsync(unixEpochNow);
        }, null, TimeSpan.FromMilliseconds(1_000), TimeSpan.FromMilliseconds(1_000));

        return Task.CompletedTask;
    }

#endregion
}

[ImplicitStreamSubscription("@")]
public class StreamConsumerGrain(ILogger<StreamConsumerGrain> logger) : Grain, IStreamConsumerGrain
{
#region Implementation of IStreamConsumerGrain

    public async Task Consume()
    {
        var guid           = Guid.Parse("d97e7fb1-e2f6-4803-b66c-965bc5d1d099");
        var streamProvider = this.GetStreamProvider(IArgonEvent.ProviderId);
        var streamId       = StreamId.Create(IArgonEvent.Namespace, guid);
        var stream         = streamProvider.GetStream<long>(streamId);

        await stream.SubscribeAsync((data, token) =>
        {
            logger.LogCritical(data.ToString());
            return Task.CompletedTask;
        });
    }

#endregion
}

#endif