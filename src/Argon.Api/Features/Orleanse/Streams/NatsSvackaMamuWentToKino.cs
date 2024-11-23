namespace Argon.Api.Features.OrleansStreamingProviders;

using Argon.Features;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;

public static class NatsSvackaMamuWentToKino
{
    public static void AddNatsStreaming(
        this IHostApplicationBuilder builder)
    {
        // TODO: Yuuki said he knows a way to make this look elegant, until then, this is the best we have
        var natsConnectionString = builder.Configuration.GetConnectionString("nats") ?? throw new ArgumentNullException("Nats");
        var natsClient           = new NatsClient(natsConnectionString);
        var natsConnection       = natsClient.Connection;
        var js                   = natsClient.CreateJetStreamContext();
        builder.Services.AddSingleton(natsClient);
        builder.Services.AddSingleton(natsConnection);
        builder.Services.AddSingleton(js);
        builder.Services.AddSingleton<NatsFuckingMomOfSvackFactory>();
        builder.Services.AddSingleton(x => x.GetRequiredService<NatsFuckingMomOfSvackFactory>().Consoomer);
        builder.Services.AddSingleton(x => x.GetRequiredService<NatsFuckingMomOfSvackFactory>().Stream);
    }
}

public class NatsFuckingMomOfSvackFactory
{
    private readonly INatsJSContext             _ctx;
    public           AsyncContainer<INatsJSStream>   Stream    { get; }
    public           AsyncContainer<INatsJSConsumer> Consoomer { get; }

    public NatsFuckingMomOfSvackFactory(INatsJSContext ctx)
    {
        _ctx       = ctx;
        Stream    = new AsyncContainer<INatsJSStream>(CreateStream);
        Consoomer = new AsyncContainer<INatsJSConsumer>(CreateComsoomer);
    }

    private async Task<INatsJSStream> CreateStream()
        => await _ctx.CreateStreamAsync(new StreamConfig("ARGON_STREAM", ["argon.streams.*"]));
    private async Task<INatsJSConsumer> CreateComsoomer() 
        => await _ctx.CreateOrUpdateConsumerAsync("ARGON_STREAM", new ConsumerConfig("streamConsoomer"));
}