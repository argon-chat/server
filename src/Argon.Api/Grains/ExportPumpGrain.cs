namespace Argon.Grains;

using Argon.Grains.Interfaces;
using Orleans.Providers;

[GenerateSerializer]
public sealed partial record ExportPumpGrainState
{
    [Id(0)]
    public HashSet<Guid> ActiveExports { get; set; } = [];
}

public class ExportPumpGrain(
    [PersistentState("export-pump-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<ExportPumpGrainState> state,
    IGrainFactory grainFactory,
    ILogger<ExportPumpGrain> logger) : Grain, IExportPumpGrain, IRemindable, IReminderJob
{
    private const string ReminderName = "export-pump";

    private static readonly ReminderSchedule PumpSchedule = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));

    string IReminderJob.ReminderName => ReminderName;

    // Nothing to pump means no reminder: an activation that finds the set empty drops one left behind.
    ReminderSchedule? IReminderJob.Schedule
        => state.State.ActiveExports.Count > 0 ? PumpSchedule : null;

    public async ValueTask RegisterActiveExportAsync(Guid userId)
    {
        state.State.ActiveExports.Add(userId);
        await state.WriteStateAsync();

        await this.RegisterOrUpdateReminder(ReminderName, PumpSchedule.FirstDelay, PumpSchedule.Period);
    }

    public async ValueTask UnregisterExportAsync(Guid userId)
    {
        state.State.ActiveExports.Remove(userId);
        await state.WriteStateAsync();

        if (state.State.ActiveExports.Count == 0)
            await this.DropReminderAsync(ReminderName);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReminderName)
            return;

        if (state.State.ActiveExports.Count == 0)
        {
            await this.DropReminderAsync(ReminderName);
            return;
        }

        logger.LogInformation("Export pump firing for {Count} active exports", state.State.ActiveExports.Count);

        var toRemove = new List<Guid>();

        foreach (var userId in state.State.ActiveExports)
        {
            try
            {
                var grain = grainFactory.GetGrain<IUserDataExportGrain>(userId);
                var inProgress = await grain.IsExportInProgressAsync();

                if (!inProgress)
                    toRemove.Add(userId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to pump export grain for user {UserId}", userId);
            }
        }

        if (toRemove.Count > 0)
        {
            foreach (var id in toRemove)
                state.State.ActiveExports.Remove(id);
            await state.WriteStateAsync();

            if (state.State.ActiveExports.Count == 0)
                await this.DropReminderAsync(ReminderName);
        }
    }
}
