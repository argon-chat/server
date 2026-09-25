namespace ArgonComplexTest.Tests;

using Argon.Services;
using ArgonComplexTest.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

/// <summary>
/// What the account-lifecycle edge fixtures reach for below a grain's public surface: its persisted
/// record, its activation, and its reminders.
/// </summary>
/// <remarks>
/// <para><b>Records.</b> Typed reads and writes go through the storage provider the grains use, by the
/// grain's own id, exactly as <c>AccountDeletionTests</c> and <c>DataExportArchiveTests</c> seed a lost
/// activation. A <em>poisoned</em> record goes underneath the provider instead: a value the provider
/// cannot deserialise is what a record written by an incompatible build looks like, and it is the one
/// deterministic way to make one grain — and only that grain — fail to activate. Every other way of
/// making a grain call throw (killing Redis, the bus, the database) is shared by the whole process and
/// would fail the neighbouring fixtures instead of the call under test.</para>
///
/// <para><b>Activations.</b> <c>IGrainManagementExtension.DeactivateOnIdle</c> ends one activation and
/// nothing else, so "the silo that held this grain went away" can be reproduced for a single grain
/// without collecting every other fixture's grains the way <c>ForceActivationCollection</c> would.</para>
///
/// <para><b>Reminders.</b> Read from the reminder table the runtime itself uses, and fired through
/// <see cref="IRemindable"/> — the interface the reminder service calls — so a tick whose period is a
/// minute or two can be delivered on demand rather than waited for.</para>
/// </remarks>
internal static class LifecycleDataHarness
{
    /// <summary>What a record this build cannot read looks like.</summary>
    private const string Poison = "{ this is not a grain record";

    private static IServiceProvider Services => ArgonTestEnvironment.Instance.Host.Services;

    public static IGrainFactory Grains => Services.GetRequiredService<IGrainFactory>();

    public static IArgonCacheDatabase Cache => Services.GetRequiredService<IArgonCacheDatabase>();

    // ── typed records ───────────────────────────────────────────────────────────────────────────

    public static async Task<T> ReadStateAsync<T>(GrainId grain, string stateName) where T : class, new()
    {
        var state = new GrainState<T>(new T());

        await Store.ReadStateAsync(stateName, grain, state);

        return state.State ?? new T();
    }

    public static async Task WriteStateAsync<T>(GrainId grain, string stateName, T seed) where T : class, new()
    {
        var state = new GrainState<T>(new T());

        await Store.ReadStateAsync(stateName, grain, state);

        state.State = seed;

        await Store.WriteStateAsync(stateName, grain, state);
    }

    private static IGrainStorage Store
        => Services.GetRequiredKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);

    // ── poisoned records ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes the grain unable to activate, and answers what its record held before.
    /// </summary>
    /// <remarks>
    /// <para>A live activation is taken down first, because it would never re-read the record — and
    /// because some grains (<c>SpaceGrain</c>) write their state on the way out, which would put a
    /// good record straight back over the poison. So the loop deactivates, poisons, and then proves
    /// the result: the next call must fail to activate. A call that succeeds means an activation read
    /// the record first, and the round is repeated.</para>
    ///
    /// <para>The key is <c>RedisStorage.GetKey</c>'s, spelled out here because that method is private.
    /// A change to it does not pass silently: the proof step fails and says so.</para>
    /// </remarks>
    public static async Task<string?> BreakActivationAsync(GrainId grain, string stateName, CancellationToken ct = default)
    {
        var key = RecordKey(grain, stateName);

        using var scope = StorageRedis.Rent();

        var db = scope.GetDatabase();

        string? original = null;
        var     captured = false;

        for (var round = 0; round < 10; round++)
        {
            try
            {
                await Grains.GetGrain<IGrainManagementExtension>(grain).DeactivateOnIdle();
            }
            catch
            {
                // Already unable to activate, which is where this is going anyway.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);

            var current = (string?)await db.StringGetAsync(key);

            if (!captured && current != Poison)
            {
                original = current;
                captured = true;
            }

            await db.StringSetAsync(key, Poison);

            if (await FailsToActivateAsync(grain))
                return original;
        }

        Assert.Fail($"could not make {grain} fail to activate by poisoning '{key}'; the storage key " +
                    "format this harness assumes is probably no longer RedisStorage's");

        return original;
    }

    /// <summary>Puts back what <see cref="BreakActivationAsync"/> replaced, or removes the record if there was none.</summary>
    public static async Task RestoreRecordAsync(GrainId grain, string stateName, string? original)
    {
        using var scope = StorageRedis.Rent();

        var db  = scope.GetDatabase();
        var key = RecordKey(grain, stateName);

        if (original is null)
            await db.KeyDeleteAsync(key);
        else
            await db.StringSetAsync(key, original);
    }

    public static async Task<bool> FailsToActivateAsync(GrainId grain)
    {
        try
        {
            await Grains.GetGrain<IGrainManagementExtension>(grain).DeactivateOnIdle();

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string RecordKey(GrainId grain, string stateName) => $"@grains/{grain.Type}/{grain}:{stateName}";

    private static IRedisPoolConnections StorageRedis
        => Services.GetRequiredKeyedService<IRedisPoolConnections>(RedisProfiles.OrleansStorage);

    // ── activations ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Ends the grain's activation, as a silo going away would, so the next call starts a new one.</summary>
    public static async Task DeactivateAsync(GrainId grain, CancellationToken ct = default)
    {
        await Grains.GetGrain<IGrainManagementExtension>(grain).DeactivateOnIdle();

        // The request is honoured once the activation is idle, which is immediately for a grain nobody
        // is calling; the pause keeps the next call from racing the deactivation it asked for.
        await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
    }

    // ── reminders ───────────────────────────────────────────────────────────────────────────────

    public static Task<ReminderEntry?> ReminderAsync(GrainId grain, string name)
        => Services.GetRequiredService<IReminderTable>().ReadRow(grain, name)!;

    /// <summary>Delivers one tick of the named reminder, the way the reminder service does.</summary>
    public static Task FireReminderAsync(GrainId grain, string name)
    {
        var now = DateTime.UtcNow;

        return Grains.GetGrain<IRemindable>(grain).ReceiveReminder(name, new TickStatus(now, TimeSpan.FromMinutes(1), now));
    }
}
