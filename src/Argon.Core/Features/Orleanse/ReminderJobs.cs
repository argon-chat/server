namespace Argon.Features;

using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Timers;

/// <summary>
/// A grain whose work arrives on one reminder. The reminder is armed on every activation from
/// <see cref="Schedule"/>, after the grain's own activation; a null schedule means the job is off and
/// a reminder left by an earlier activation is dropped.
/// </summary>
/// <remarks>
/// Not a grain interface on purpose, since those cannot carry properties. The class still implements
/// <see cref="IRemindable"/> to receive the ticks.
/// </remarks>
public interface IReminderJob
{
    string ReminderName { get; }

    /// <summary>
    /// Read on every activation rather than once, so configuration that turns a job off can stop a
    /// reminder registered by a pod that has since been replaced.
    /// </summary>
    ReminderSchedule? Schedule { get; }
}

public readonly record struct ReminderSchedule(TimeSpan FirstDelay, TimeSpan Period);

/// <summary>
/// Arms <see cref="IReminderJob"/> grains through the shared activation setup Orleans 10.4 added:
/// selected once per grain type, run for every activation, no base class.
/// </summary>
internal sealed class ReminderJobConfigurator(GrainClassMap grainClasses, ILogger<ReminderJobConfigurator> logger)
    : IConfigureGrainTypeComponents
{
    public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
    {
        if (!grainClasses.TryGetGrainClass(grainType, out var grainClass) || !typeof(IReminderJob).IsAssignableFrom(grainClass))
            return;

        if (!typeof(IRemindable).IsAssignableFrom(grainClass))
            throw new InvalidOperationException(
                $"{grainClass.Name} is an {nameof(IReminderJob)} but not {nameof(IRemindable)}; nothing would receive its ticks.");

        // Last rather than Activate: OnActivateAsync has run, so a schedule may read what it set up.
        shared.AddActivationSetup(context => context.ObservableLifecycle.Subscribe(
            nameof(ReminderJobConfigurator), GrainLifecycleStage.Last, _ => ArmAsync(context)));
    }

    private async Task ArmAsync(IGrainContext context)
    {
        var job       = (IReminderJob)context.GrainInstance!;
        var reminders = context.ActivationServices.GetRequiredService<IReminderRegistry>();

        if (job.Schedule is { } schedule)
        {
            await reminders.RegisterOrUpdateReminder(context.GrainId, job.ReminderName, schedule.FirstDelay, schedule.Period);
            logger.LogInformation("{Grain}: reminder {Reminder} armed, first tick in {First}, then every {Period}",
                context.GrainId, job.ReminderName, schedule.FirstDelay, schedule.Period);
            return;
        }

        if (await ReminderJobExtensions.DropAsync(reminders, context.GrainId, job.ReminderName))
            logger.LogInformation("{Grain}: reminder {Reminder} dropped, the job is off", context.GrainId, job.ReminderName);
    }
}

public static class ReminderJobExtensions
{
    public static IServiceCollection AddReminderJobs(this IServiceCollection services)
        => services.AddSingleton<IConfigureGrainTypeComponents, ReminderJobConfigurator>();

    /// <summary>Drops the named reminder if there is one. Already gone counts as dropped.</summary>
    public static Task<bool> DropReminderAsync(this IGrainBase grain, string name)
        => DropAsync(grain.GrainContext.ActivationServices.GetRequiredService<IReminderRegistry>(), grain.GrainContext.GrainId, name);

    internal static async Task<bool> DropAsync(IReminderRegistry reminders, GrainId grainId, string name)
    {
        try
        {
            if (await reminders.GetReminder(grainId, name) is not { } reminder)
                return false;

            await reminders.UnregisterReminder(grainId, reminder);
            return true;
        }
        catch (ReminderException)
        {
            return false;
        }
    }
}
