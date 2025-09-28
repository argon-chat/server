namespace Argon.Features.Integrations.Phones.Telegram;

public static class TelegramGatewayExtensions
{
    public static void AddTelegramGateway(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<TelegramGatewayOptions>(builder.Configuration.GetSection("Phone:TelegramGateway"));
        builder.Services.AddScoped<TelegramGateway>();
    }
}