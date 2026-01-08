namespace Argon.Core.Features.Logic;

public static class NotificationCounterFeature
{
    public static WebApplicationBuilder AddNotificationCounterFeature(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<INotificationCounterService, NotificationCounterService>();
        return builder;
    }
}
