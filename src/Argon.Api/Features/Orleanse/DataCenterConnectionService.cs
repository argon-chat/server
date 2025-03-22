namespace Argon.Features;

public class DataCenterConnectionService(IArgonDcRegistry registry, ILogger<DataCenterConnectionService> logger)
    : BackgroundService
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var kv in registry.GetAll())
            {
                if (kv.Value.status == ArgonDataCenterStatus.ONLINE)
                    continue;
                //Task.Run(async () =>
                //{
                //    while (!stoppingToken.IsCancellationRequested)
                //    {
                //        try
                //        {
                //            var cc = kv.Value.serviceProvider.GetRequiredService<IClusterClient>();

                //            if (cc is IHostedService hs)
                //                await hs.StartAsync(stoppingToken).ConfigureAwait(false);
                //        }
                //        catch (Exception e)
                //        {
                //            await 
                //        }
                //    }
                //});


                try
                {
                    var cc = kv.Value.serviceProvider.GetRequiredService<IClusterClient>();

                    if (cc is IHostedService hs)
                        await hs.StartAsync(stoppingToken).ConfigureAwait(false);

                    logger.LogInformation($"DC [{kv.Key}] marked ONLINE");
                    registry.Upsert(kv.Value with
                    {
                        status = ArgonDataCenterStatus.ONLINE
                    });
                }
                catch (Exception e)
                {
                    logger.LogDebug(e, "Failed to start cluster client");
                    registry.Upsert(kv.Value with
                    {
                        status = ArgonDataCenterStatus.OFFLINE
                    });
                    logger.LogWarning($"DC [{kv.Key}] connect failed, will retry");
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}