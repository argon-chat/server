namespace Argon.Features.Integrations.Klipy;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http.Resilience;

public static class KlipyFeature
{
    public static IServiceCollection AddKlipyFeature(this WebApplicationBuilder builder)
    {
        builder.Services.AddHttpClient<IKlipyService, KlipyService>()
           .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 3;
                o.Retry.UseJitter = true;
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            });
        return builder.Services;
    }
}
