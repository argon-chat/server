namespace Argon.Features.Scheduling;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class SchedulingFeatureExtensions
{
    public static IHostApplicationBuilder AddScheduledTasks(this IHostApplicationBuilder builder)
    {
        // Register task implementations
        builder.Services.AddSingleton<AutoDeleteScheduledTask>();
        builder.Services.AddSingleton<IScheduledTask>(sp => sp.GetRequiredService<AutoDeleteScheduledTask>());

        // ExportPumpGrain is kept as-is — it uses Orleans persistent state + reminders
        // and is naturally multi-DC safe (per-DC singleton in independent clusters).

        // Register the orchestrator
        builder.Services.AddHostedService<ScheduledTasksService>();

        return builder;
    }
}
