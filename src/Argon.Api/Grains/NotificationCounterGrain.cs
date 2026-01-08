namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Orleans.Concurrency;

[StatelessWorker]
public class NotificationCounterGrain(
    INotificationCounterService notificationCounterService,
    ILogger<NotificationCounterGrain> logger) : Grain, INotificationCounterGrain
{
    public async Task<NotificationCounters> GetAllCountersAsync()
    {
        var userId = this.GetPrimaryKey();
        return await notificationCounterService.GetAllCountersAsync(userId);
    }
}
