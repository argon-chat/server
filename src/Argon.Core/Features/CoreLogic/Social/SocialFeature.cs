namespace Argon.Api.Features.CoreLogic.Social;

public static class SocialFeature
{
    public static IHostApplicationBuilder AddSocialIntegrations(this IHostApplicationBuilder builder)
    {
        //builder.Services.Configure<TelegramSocialOptions>(builder.Configuration.GetSection("social:telegram"));
        //builder.Services.AddSingleton<TelegramSocialBounder>();
        return builder;
    }
}