namespace Argon.Features.Sagas;

using Argon.Api.Features.CoreLogic.Otp;
using Core.Features.Integrations.Phones;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

public static class SagasFeature
{
    public static void AddSagas(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<TemporalClientConnectOptions>(builder.Configuration.GetSection("Sagas"));
        builder.Services.Configure<TemporalWorkerServiceOptions>(builder.Configuration.GetSection("Sagas"));
        builder.Services.AddTemporalClient();

        builder.Services
           .AddHostedTemporalWorker("otp-queue")
           .AddWorkflow<PhoneOtpWorkflow>()
           .AddWorkflow<OtpWorkflow>()
           .AddTransientActivities<OtpActivities>()
           .AddTransientActivities<PhoneActivities>();
    }
}