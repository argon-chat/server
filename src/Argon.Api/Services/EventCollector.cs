namespace Argon.Services;

public class EventCollector([FromKeyedServices("event.collector.cfg")] Action<IEventConsumer> configuration) : IEventCollector
{
    private readonly Dictionary<string, Func<object, IEventConsumer, Task>> executers = new();

    public void On<T>(Func<T, IEventConsumerContext, Task> on) where T : IArgonEvent
        => executers.Add(typeof(T).Name, (o, consumer) => on((T)o, GetContext()));

    private IEventConsumerContext GetContext()
    {
        var current = ArgonTransportContext.Current;

        return new EventConsumerContext(this, current.GetClusterClient())
        {
            UserId = current.User.id,
            SessionId = current.GetSessionId()
        };
    }

    public async Task ExecuteEventAsync<T>(T val) where T : IArgonEvent
    {
        if (executers.TryGetValue(val.EventKey, out var caller))
            await caller(val, this);
    }

    public void PostConfigure()
        => configuration(this);
}

public static class EventCollectorFeature
{
    public static IServiceCollection AddEventCollectorFeature(this WebApplicationBuilder builder, Action<IEventConsumer> onConfigure)
    {
        builder.Services.AddSingleton<IEventCollector, EventCollector>();
        builder.Services.AddKeyedSingleton("event.collector.cfg", onConfigure);
        return builder.Services;
    }

    public static WebApplication UseEventCollectorFeature(this WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() => { app.Services.GetRequiredService<IEventCollector>().PostConfigure(); });
        return app;
    }
}

public static class EventConfigurator
{
    public static void Configure(IEventConsumer consumer)
    {
        consumer.On<HeartBeatEvent>(async (ev, ctx) =>
        {
            if (ctx.SessionId == Guid.Empty)
                return;
            await ctx.ClusterClient.GetGrain<IUserSessionGrain>(ctx.SessionId).HeartBeatAsync(ev.status);
        });
    }
}

public interface IEventCollector : IEventConsumer
{
    Task ExecuteEventAsync<T>(T val) where T : IArgonEvent;
    void PostConfigure();
}

public interface IEventConsumer
{
    void On<T>(Func<T, IEventConsumerContext, Task> on) where T : IArgonEvent;
}

public struct EventConsumerContext(IEventCollector collector, IClusterClient client) : IEventConsumerContext
{
    public Guid           UserId        { get; set; }
    public Guid           SessionId     { get; set; }
    public IClusterClient ClusterClient { get; } = client;

    public Task SendEventAsync<T>(T value) where T : IArgonEvent
        => throw new NotImplementedException();
}

public interface IEventConsumerContext
{
    Guid           UserId        { get; }
    Guid           SessionId     { get; }
    IClusterClient ClusterClient { get; }
    Task           SendEventAsync<T>(T value) where T : IArgonEvent;
}