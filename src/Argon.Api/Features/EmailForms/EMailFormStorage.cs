namespace Argon.Api.Features.EmailForms;

using System.Collections.Concurrent;
using Services;

public static class JwtFeature
{
    public static IServiceCollection AddEMailForms(this WebApplicationBuilder builder)
    {
        builder.Services.AddHostedService<EMailFormLoader>();
        builder.Services.AddSingleton<EMailFormStorage>();
        return builder.Services;
    }
}
