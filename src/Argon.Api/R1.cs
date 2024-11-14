namespace Argon.Api;

public class R1(ILogger<R1> logger, IClusterClient client) : BackgroundService
{
#region Overrides of BackgroundService

    protected async override Task ExecuteAsync(CancellationToken stoppingToken) => await Task.Yield();
    // var guid              = Guid.Parse("d97e7fb1-e2f6-4803-b66c-965bc5d1d099");
    // var clientArgonStream = new ClientArgonStream<long>();
    // var provider          = client.GetStreamProvider(IArgonEvent.ProviderId);
    // var stream            = provider.GetStream<long>(StreamId.Create(IArgonEvent.Namespace, guid));
    // var bound             = await clientArgonStream.BindClient(stream);
    // await foreach (var argonEvent in bound.AsRpcStream())
    // {
    //     logger.LogCritical(argonEvent.ToString());
    // }

#endregion
}