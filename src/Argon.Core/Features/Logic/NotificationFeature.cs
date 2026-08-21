namespace Argon.Core.Features.Logic;

public static class NotificationFeature
{
    public static WebApplicationBuilder AddNotificationFeature(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IReadStateService, ReadStateService>();
        builder.Services.AddScoped<IMuteSettingsService, MuteSettingsService>();
        builder.Services.AddScoped<ISystemNotificationService, SystemNotificationService>();
        builder.Services.AddScoped<IBadgeAggregationService, BadgeAggregationService>();
        return builder;
    }
}
