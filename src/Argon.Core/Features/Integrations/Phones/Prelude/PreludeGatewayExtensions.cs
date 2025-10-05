namespace Argon.Features.Integrations.Phones.Prelude;

public static class PreludeGatewayExtensions
{
    public static void AddPreludeGateway(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<PreludeGatewayOptions>(builder.Configuration.GetSection("Phone:Prelude"));
        builder.Services.AddScoped<PreludeGateway>();
    }
}