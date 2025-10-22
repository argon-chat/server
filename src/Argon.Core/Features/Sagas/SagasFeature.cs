namespace Argon.Features.Sagas;

using Argon.Api.Features.CoreLogic.Otp;
using Core.Features.Integrations.Phones;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

public static class SagasFeature
{
    public static void AddSagas(this WebApplicationBuilder builder)
    {
        var sagasSection = builder.Configuration.GetSection("Sagas");
        var enabled      = sagasSection.GetValue<bool>("Enabled");

        if (!enabled)
            return;

        builder.Services.Configure<TemporalClientConnectOptions>(sagasSection);
        builder.Services.Configure<TemporalWorkerServiceOptions>(sagasSection);

        builder.Services.AddTemporalClient();

        builder.Services
           .AddHostedTemporalWorker("otp-queue")
           .AddWorkflow<PhoneOtpWorkflow>()
           .AddWorkflow<OtpWorkflow>()
           .AddTransientActivities<OtpActivities>()
           .AddTransientActivities<PhoneActivities>();
    }
}