namespace Argon.Features.Sagas;

using Argon.Api.Features.CoreLogic.Otp;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;

public static class SagasFeature
{
    public static void AddSagas(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<TemporalClientConnectOptions>(builder.Configuration.GetSection("Sagas"));
        builder.Services.Configure<TemporalWorkerServiceOptions>(builder.Configuration.GetSection("Sagas"));
        builder.Services.AddTemporalClient();
        builder.Services
           .AddHostedTemporalWorker("argon-task-queue")
           .AddWorkflow<OtpWorkflow>()
           .AddTransientActivities<OtpActivities>();
    }
}