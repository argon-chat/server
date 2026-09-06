namespace ArgonComplexTest.Infrastructure.Account;

using Argon.Entities;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Account state no API can produce, written straight into the database.
/// </summary>
/// <remarks>
/// <para>The repository's rule is that <c>DbContext</c> lives in grains, and the suite honours it:
/// everything a fixture can reach through Ion or through a grain, it reaches that way. What is left
/// over is state that exists only because time passed or because an operator acted — a last login
/// thirteen months ago, an account under lockdown, an active Ultima subscription — and there is no
/// call that produces any of it. Seeding those directly is the alternative to not testing the
/// branches that read them.</para>
///
/// <para>Every helper here writes the smallest row that makes the branch true and says which branch
/// that is, so a reader of a fixture can tell at a glance whether the setup is honest — a seed that
/// quietly arranged more than the test claims would make the assertion mean something else.</para>
/// </remarks>
public static class AccountSeed
{
    /// <summary>A fresh context from the host's own factory.</summary>
    public static Task<ApplicationDbContext> NewDbAsync(CancellationToken ct = default)
        => ArgonTestEnvironment.Instance.Host.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

    /// <summary>
    /// The user row as it stands, including one an executed deletion has anonymised.
    /// </summary>
    /// <remarks>
    /// <para><c>IgnoreQueryFilters</c>, and it is the entire reason this helper exists rather than a
    /// one-liner in each fixture. <c>ApplicationDbContext.UseSoftDeleteCompatibility</c> puts a
    /// global <c>!IsDeleted</c> filter on <em>every</em> <c>ArgonEntity</c>, <c>UserEntity</c>
    /// included, so an ordinary <c>db.Users.FirstOrDefault(u =&gt; u.Id == id)</c> answers
    /// <see langword="null"/> for a deleted account — which reads as "the row was removed" and is
    /// exactly the wrong conclusion: deletion anonymises in place and the row is still there.</para>
    ///
    /// <para>Assert on the anonymisation through this. A fixture that reaches for the filtered set
    /// gets a null and proves nothing about what the deletion actually wrote.</para>
    /// </remarks>
    public static async Task<UserEntity?> ReadUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await NewDbAsync(ct);

        return await db.Users
           .IgnoreQueryFilters()
           .AsNoTracking()
           .FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    /// <summary>
    /// Whether the account is visible to an ordinary query — i.e. through the global soft-delete
    /// filter every <c>ArgonEntity</c> carries.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="ReadUserAsync"/>, and worth its own name: "the row still exists"
    /// and "anything reading it the normal way can still see it" are different facts, and after a
    /// deletion they have different answers. A test about what other users see wants this one.
    /// </remarks>
    public static async Task<bool> IsVisibleAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await NewDbAsync(ct);

        return await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, ct);
    }

    /// <summary>
    /// Gives the account a device whose last login is <paramref name="lastLogin"/>.
    /// </summary>
    /// <remarks>
    /// What the inactivity scan reads. <c>AutoDeleteSchedulerGrain</c> takes the newest
    /// <c>DeviceHistories.LastLoginTime</c> and falls back to <c>CreatedAt</c> when there is none, so
    /// a test about "inactive for longer than the threshold" has to write a row here — backdating
    /// <c>CreatedAt</c> alone would be testing the fallback rather than the rule.
    /// </remarks>
    public static async Task BackdateLastLoginAsync(
        Guid userId, DateTimeOffset lastLogin, string machineId = "seeded-device", CancellationToken ct = default)
    {
        await using var db = await NewDbAsync(ct);

        var existing = await db.DeviceHistories
           .FirstOrDefaultAsync(d => d.UserId == userId && d.MachineId == machineId, ct);

        if (existing is null)
            db.DeviceHistories.Add(new UserDeviceHistoryEntity
            {
                UserId        = userId,
                MachineId     = machineId,
                LastLoginTime = lastLogin,
                LastKnownIP   = "127.0.0.1",
                RegionAddress = "us",
                AppId         = "integration-tests",
                DeviceType    = DeviceTypeKind.Unknown
            });
        else
            existing.LastLoginTime = lastLogin;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes every device history row for the account, so the scan falls back to
    /// <c>CreatedAt</c>.
    /// </summary>
    public static async Task ClearLoginHistoryAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await NewDbAsync(ct);

        await db.DeviceHistories.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Backdates when the account itself was created.</summary>
    /// <remarks>
    /// The other half of the inactivity rule: with no device history the scan measures from here, and
    /// a user registered seconds ago is never inactive however the threshold is set.
    /// </remarks>
    public static async Task BackdateCreatedAtAsync(Guid userId, DateTimeOffset createdAt, CancellationToken ct = default)
    {
        await using var db = await NewDbAsync(ct);

        await db.Users.Where(u => u.Id == userId)
           .ExecuteUpdateAsync(set => set.SetProperty(u => u.CreatedAt, createdAt), ct);
    }

    /// <summary>
    /// Writes the account's auto-delete preference — the setting the privacy screen exposes.
    /// </summary>
    /// <remarks>
    /// <c>SetAutoDeletePeriod</c> over Ion is the real way in and fixtures should prefer it; this
    /// exists for the case that surface cannot express, which is a row with
    /// <c>Enabled = false</c> — an account that explicitly turned auto-deletion <em>off</em>.
    /// </remarks>
    public static async Task SetAutoDeleteAsync(
        Guid userId, int? months, bool enabled, CancellationToken ct = default)
    {
        await using var db = await NewDbAsync(ct);

        var existing = await db.AutoDeleteSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);

        if (existing is null)
            db.AutoDeleteSettings.Add(new UserAutoDeleteSettingEntity
            {
                Id      = Guid.NewGuid(),
                UserId  = userId,
                Months  = months,
                Enabled = enabled
            });
        else
        {
            existing.Months  = months;
            existing.Enabled = enabled;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Puts the account under lockdown — the guard <c>RequestDeletionAsync</c> checks third.
    /// </summary>
    /// <remarks>
    /// An operator action in production, with no self-service path at all, so there is nothing to
    /// drive but the row. <paramref name="reason"/> is anything other than <c>NONE</c>; the grain
    /// only compares against that.
    /// </remarks>
    public static async Task LockAsync(
        Guid userId,
        LockdownReason reason = LockdownReason.UNDER_INVESTIGATION,
        CancellationToken ct = default)
    {
        await using var db = await NewDbAsync(ct);

        await db.Users.Where(u => u.Id == userId)
           .ExecuteUpdateAsync(set => set.SetProperty(u => u.LockdownReason, reason), ct);
    }

    /// <summary>Clears a lockdown written by <see cref="LockAsync"/>.</summary>
    public static async Task UnlockAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await NewDbAsync(ct);

        await db.Users.Where(u => u.Id == userId)
           .ExecuteUpdateAsync(set => set.SetProperty(u => u.LockdownReason, LockdownReason.NONE), ct);
    }

    /// <summary>
    /// Flips the account's Ultima flag — the guard both deletion entry points check.
    /// </summary>
    /// <remarks>
    /// The flag rather than a subscription row, because the flag is what the grain reads. A test
    /// about the <em>subscription</em> (payments, expiry, the Xsolla flow) belongs in
    /// <c>UltimaTests</c> and should go through that machinery; this is for "deletion is refused
    /// while a paid plan is live", which is one boolean.
    /// </remarks>
    public static async Task SetUltimaAsync(Guid userId, bool active, CancellationToken ct = default)
    {
        await using var db = await NewDbAsync(ct);

        await db.Users.Where(u => u.Id == userId)
           .ExecuteUpdateAsync(set => set.SetProperty(u => u.HasActiveUltima, active), ct);
    }
}
