namespace ArgonSharedLogicTest;

using Argon.Features.Clustering;
using Argon.Features.Logic;
using ArgonSharedLogicTest.Clustering;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Carries the account-lifecycle options under sections of their own.
/// </summary>
/// <remarks>
/// Not <c>AccountDeletion</c> and <c>DataExport</c>: the real feature owns those names, and a second
/// claim on one is a rule the product enforces (<c>ShippedConfigurationTests</c> refuses two owners
/// for a section). The rules under test read the values, not the path they arrived by.
/// </remarks>
public sealed class AccountLifecycleOptionsFeature : IArgonFeature
{
    public const string DeletionSection = "AccountDeletionUnderTest";
    public const string ExportSection   = "DataExportUnderTest";

    public static void Describe(IFeatureDescriptor d)
        => d.Named("account-lifecycle-options")
            .Options<AccountDeletionOptions>(DeletionSection)
            .Options<DataExportOptions>(ExportSection);
}

public sealed class AccountLifecycleOptionsRole : IArgonRole
{
    public static ArgonRoleId Id => new("account-lifecycle-options");

    public bool IsClient => false;

    public void OnFeatures(IArgonFeatureRegistry features)
        => features.Add<AccountLifecycleOptionsFeature>();
}

/// <summary>
/// The rules an account-deletion and data-export policy is checked against before a role starts, and
/// the defaults those rules are supposed to accept.
/// </summary>
/// <remarks>
/// <para><b>The defaults are the load-bearing half.</b> Both classes exist because the integration
/// suite needs to compress these clocks, and a refactor that turns five constants into five settings
/// is exactly the change that can silently move production: a grace that became 30 <em>hours</em>
/// because a <c>TimeSpan</c> was written where an <c>int</c> was meant, a rate limit that became 30
/// seconds, a batch that became 20. Nothing at run time would notice — the host would start, the
/// grains would run, and accounts would be deleted a month early. So the first cases below pin the
/// resolved values against the literals the grains used to carry, and they are the cases to look at
/// first when this fixture goes red.</para>
///
/// <para><b>The precedence half is the other silent failure.</b> Two spellings of the same setting is
/// a footgun unless the rule is stated and tested: a deployment that has always written
/// <c>GracePeriodDays: 45</c> must keep getting 45 days, whatever any default says, and a host that
/// needs eight seconds must be able to say so without the day-shaped name overriding it. Both
/// directions are asserted, including the case that broke the first draft of this — arrays, where the
/// configuration binder <em>appends</em> to a populated default instead of replacing it, which is why
/// both list properties ship empty and the real defaults live in static fields.</para>
///
/// <para>Through <see cref="FeatureConfigurationValidator"/> rather than by calling
/// <c>Validate</c> directly, for the reason <see cref="ReportSystemOptionsValidatorTests"/> gives:
/// the binder is part of what is under test.</para>
/// </remarks>
[TestFixture]
public class AccountLifecycleOptionsTests
{
    private static (string[] Errors, string[] Warnings) Validate(params (string Key, string? Value)[] overrides)
    {
        var report = FeatureConfigurationValidator.Validate(
            ConfigurationFixtures.Role<AccountLifecycleOptionsRole>(),
            ConfigurationFixtures.From(overrides));

        return (report.Errors.Select(d => d.ToString()).ToArray(),
                report.Warnings.Select(d => d.ToString()).ToArray());
    }

    private static AccountDeletionOptions Deletion(params (string Key, string? Value)[] overrides)
        => Bind<AccountDeletionOptions>(AccountLifecycleOptionsFeature.DeletionSection, overrides);

    private static DataExportOptions Export(params (string Key, string? Value)[] overrides)
        => Bind<DataExportOptions>(AccountLifecycleOptionsFeature.ExportSection, overrides);

    private static T Bind<T>(string section, (string Key, string? Value)[] overrides) where T : class, new()
    {
        var bound = new T();

        ConfigurationFixtures.From(overrides).GetSection(section).Bind(bound);

        return bound;
    }

    // ── the defaults are the constants that shipped ─────────────────────────────────────────────

    /// <summary>
    /// Thirty days, reminders at seven and one, polled every six hours — the literals
    /// <c>AccountDeletionGrain</c> carried before any of this was configurable.
    /// </summary>
    [Test]
    public void The_deletion_defaults_are_the_values_that_shipped()
    {
        var options = Deletion();

        Assert.Multiple(() =>
        {
            Assert.That(options.EffectiveGracePeriod, Is.EqualTo(TimeSpan.FromDays(30)));
            Assert.That(options.EffectiveReminders, Is.EqualTo(new[] { TimeSpan.FromDays(7), TimeSpan.FromDays(1) }));
            Assert.That(options.CheckInterval, Is.EqualTo(TimeSpan.FromHours(6)));
            Assert.That(options.AutoDeleteEnabled, Is.False, "inactivity deletion is opt-in");
        });
    }

    /// <summary>
    /// A tick every thirty seconds with the first after one, a thirty-day limit, a forty-eight-hour
    /// archive, two hundred messages a batch — <c>UserDataExportGrain</c>'s former constants.
    /// </summary>
    [Test]
    public void The_export_defaults_are_the_values_that_shipped()
    {
        var options = Export();

        Assert.Multiple(() =>
        {
            Assert.That(options.ProcessInterval, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(options.FirstTickDelay, Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(options.RateLimitPeriod, Is.EqualTo(TimeSpan.FromDays(30)));
            Assert.That(options.ArchiveTtl, Is.EqualTo(TimeSpan.FromHours(48)));
            Assert.That(options.MessageBatchSize, Is.EqualTo(200));
        });
    }

    /// <summary>Nothing configured at all has to start, on both sections.</summary>
    [Test]
    public void The_defaults_are_a_working_policy()
    {
        var (errors, warnings) = Validate();

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty, "a host with no account-lifecycle configuration has to start");
            Assert.That(warnings, Is.Empty);
        });
    }

    // ── precedence between the two spellings ────────────────────────────────────────────────────

    /// <summary>
    /// The name a deployment already writes still decides, whatever the duration form says.
    /// </summary>
    [Test]
    public void The_day_shaped_names_win_when_both_are_set()
    {
        var options = Deletion(
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:GracePeriodDays", "45"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:GracePeriod", "00:00:08"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderDays:0", "14"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderBefore:0", "00:00:06"));

        Assert.Multiple(() =>
        {
            Assert.That(options.EffectiveGracePeriod, Is.EqualTo(TimeSpan.FromDays(45)));
            Assert.That(options.EffectiveReminders, Is.EqualTo(new[] { TimeSpan.FromDays(14) }));
        });
    }

    /// <summary>
    /// The duration form is what a host with a sub-day grace sets, and it is honoured when the
    /// day-shaped name is left alone.
    /// </summary>
    [Test]
    public void The_duration_forms_are_used_when_the_day_shaped_names_are_unset()
    {
        var options = Deletion(
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:GracePeriod", "00:00:08"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderBefore:0", "00:00:06"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderBefore:1", "00:00:03"));

        Assert.Multiple(() =>
        {
            Assert.That(options.EffectiveGracePeriod, Is.EqualTo(TimeSpan.FromSeconds(8)));
            Assert.That(options.EffectiveReminders,
                Is.EqualTo(new[] { TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(3) }));
        });
    }

    /// <summary>
    /// A configured reminder list replaces the shipped one rather than being appended to it.
    /// </summary>
    /// <remarks>
    /// The binder's array behaviour, pinned because it is counter-intuitive and because getting it
    /// wrong is invisible: a default of <c>[7, 1]</c> on the property would make a deployment writing
    /// <c>ReminderDays:0</c> and <c>:1</c> end up with four thresholds, two of them the defaults it
    /// thought it had replaced — and every one of them would fire.
    /// </remarks>
    [Test]
    public void A_configured_reminder_list_replaces_the_shipped_one()
    {
        var options = Deletion(
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderDays:0", "3"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderDays:1", "2"));

        Assert.That(options.EffectiveReminders,
            Is.EqualTo(new[] { TimeSpan.FromDays(3), TimeSpan.FromDays(2) }));
    }

    /// <summary>
    /// A whole-day threshold is remembered by its day count, so state written before this existed —
    /// and everything a production host writes — reads back unchanged.
    /// </summary>
    [Test]
    public void A_whole_day_reminder_keeps_the_key_the_old_state_used()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AccountDeletionOptions.ReminderKey(TimeSpan.FromDays(7)), Is.EqualTo(7));
            Assert.That(AccountDeletionOptions.ReminderKey(TimeSpan.FromDays(1)), Is.EqualTo(1));

            // Sub-day thresholds cannot collide with a day count, and two of them stay distinct even
            // though both floor to zero days.
            Assert.That(AccountDeletionOptions.ReminderKey(TimeSpan.FromSeconds(6)), Is.EqualTo(-6));
            Assert.That(AccountDeletionOptions.ReminderKey(TimeSpan.FromSeconds(3)), Is.EqualTo(-3));
            Assert.That(AccountDeletionOptions.ReminderKey(TimeSpan.FromHours(12)), Is.EqualTo(-43200));
        });
    }

    // ── the compressed host is a policy the validator accepts ───────────────────────────────────

    /// <summary>
    /// The integration host's own numbers pass, which is what lets it run the production validators.
    /// </summary>
    /// <remarks>
    /// Here rather than only in the integration suite because a set of values the validator rejects
    /// fails the host at start-up, and a host that will not start reports as "every account test is
    /// broken" rather than as "these five numbers disagree".
    /// </remarks>
    [Test]
    public void The_compressed_integration_values_are_accepted()
    {
        var (errors, _) = Validate(
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:GracePeriod", "00:00:08"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderBefore:0", "00:00:06"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderBefore:1", "00:00:03"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:CheckInterval", "00:00:02"),
            ($"{AccountLifecycleOptionsFeature.ExportSection}:ProcessInterval", "00:00:01"),
            ($"{AccountLifecycleOptionsFeature.ExportSection}:FirstTickDelay", "00:00:01"),
            ($"{AccountLifecycleOptionsFeature.ExportSection}:RateLimitPeriod", "00:00:20"),
            ($"{AccountLifecycleOptionsFeature.ExportSection}:ArchiveTtl", "00:00:40"),
            ($"{AccountLifecycleOptionsFeature.ExportSection}:MessageBatchSize", "5"));

        Assert.That(errors, Is.Empty);
    }

    // ── nonsense is refused ─────────────────────────────────────────────────────────────────────

    /// <summary>A grace of zero deletes the account on the first poll after the request.</summary>
    [Test]
    public void A_grace_period_of_zero_is_refused()
    {
        var (errors, _) = Validate(($"{AccountLifecycleOptionsFeature.DeletionSection}:GracePeriodDays", "0"));

        Assert.That(errors, Has.Some.Contains(nameof(AccountDeletionOptions.GracePeriod)).IgnoreCase);
    }

    /// <summary>
    /// A reminder at or beyond the grace is sent the moment the deletion is scheduled, which is not a
    /// warning, it is a duplicate confirmation.
    /// </summary>
    [Test]
    public void A_reminder_further_out_than_the_grace_is_refused()
    {
        var (errors, _) = Validate(
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:GracePeriodDays", "7"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderDays:0", "30"));

        Assert.That(errors, Has.Some.Contains(nameof(AccountDeletionOptions.ReminderDays)).IgnoreCase);
    }

    /// <summary>
    /// A poll wider than the closest reminder steps over it: the last warning before deletion is
    /// never sent, and nothing anywhere records that it was not.
    /// </summary>
    [Test]
    public void A_poll_wider_than_the_closest_reminder_is_refused()
    {
        var (errors, _) = Validate(
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:GracePeriodDays", "30"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderDays:0", "1"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:CheckInterval", "2.00:00:00"));

        Assert.That(errors, Has.Some.Contains(nameof(AccountDeletionOptions.CheckInterval)).IgnoreCase);
    }

    /// <summary>The same threshold twice is a reminder that silently goes missing.</summary>
    [Test]
    public void A_repeated_reminder_threshold_is_refused()
    {
        var (errors, _) = Validate(
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderDays:0", "7"),
            ($"{AccountLifecycleOptionsFeature.DeletionSection}:ReminderDays:1", "7"));

        Assert.That(errors, Has.Some.Contains(nameof(AccountDeletionOptions.ReminderDays)).IgnoreCase);
    }

    /// <summary>An export batch of zero produces an archive with none of the messages in it.</summary>
    [Test]
    public void An_export_batch_of_zero_is_refused()
    {
        var (errors, _) = Validate(($"{AccountLifecycleOptionsFeature.ExportSection}:MessageBatchSize", "0"));

        Assert.That(errors, Has.Some.Contains(nameof(DataExportOptions.MessageBatchSize)).IgnoreCase);
    }

    /// <summary>A tick of zero is a timer Orleans will not register.</summary>
    [Test]
    public void An_export_tick_of_zero_is_refused()
    {
        var (errors, _) = Validate(($"{AccountLifecycleOptionsFeature.ExportSection}:ProcessInterval", "00:00:00"));

        Assert.That(errors, Has.Some.Contains(nameof(DataExportOptions.ProcessInterval)).IgnoreCase);
    }

    /// <summary>
    /// An archive that expires inside the cadence that produced it is expired before anything can
    /// report it ready.
    /// </summary>
    [Test]
    public void An_archive_shorter_lived_than_a_tick_is_refused()
    {
        var (errors, _) = Validate(
            ($"{AccountLifecycleOptionsFeature.ExportSection}:ProcessInterval", "00:01:00"),
            ($"{AccountLifecycleOptionsFeature.ExportSection}:ArchiveTtl", "00:00:30"));

        Assert.That(errors, Has.Some.Contains(nameof(DataExportOptions.ArchiveTtl)).IgnoreCase);
    }

    /// <summary>
    /// A "fast" first tick slower than the steady one is not an optimisation, it is a delay in front
    /// of every export.
    /// </summary>
    [Test]
    public void A_first_tick_slower_than_the_steady_one_is_refused()
    {
        var (errors, _) = Validate(
            ($"{AccountLifecycleOptionsFeature.ExportSection}:ProcessInterval", "00:00:10"),
            ($"{AccountLifecycleOptionsFeature.ExportSection}:FirstTickDelay", "00:01:00"));

        Assert.That(errors, Has.Some.Contains(nameof(DataExportOptions.FirstTickDelay)).IgnoreCase);
    }
}
