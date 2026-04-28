namespace Argon.Core.Features.Integrations.Xsolla;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http.Resilience;

public static class XsollaFeature
{
    public static IServiceCollection AddXsollaFeature(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration.GetSection("Xsolla");
        builder.Services.Configure<XsollaOptions>(cfg);
        builder.Services.AddHttpClient<IXsollaService, XsollaService>()
           .AddStandardResilienceHandler(o =>
            {
                // 3 retries with exponential backoff (1s, 2s, 4s) for transient HTTP errors
                o.Retry.MaxRetryAttempts = 3;
                o.Retry.UseJitter = true;
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            });
        return builder.Services;
    }
}
