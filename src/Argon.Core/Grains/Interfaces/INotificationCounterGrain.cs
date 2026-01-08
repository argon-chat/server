namespace Argon.Grains.Interfaces;

using Core.Features.Logic;

[Alias("Argon.Grains.Interfaces.INotificationCounterGrain")]
public interface INotificationCounterGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetAllCountersAsync))]
    Task<NotificationCounters> GetAllCountersAsync();
}
