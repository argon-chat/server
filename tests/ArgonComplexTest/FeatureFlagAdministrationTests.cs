namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The write side of <c>FeatureFlagGrain</c> — what the operator console and a dialed USSD code do
/// to a flag — and the corners of evaluation <see cref="FeatureFlagTests"/> leaves alone: percentage
/// rollout, forced variants below the user scope, and variant documents that cannot be used.
/// </summary>
/// <remarks>
/// Every flag here has an id and a USSD code of its own, so the fixture can run beside every other
/// one and against a database reused from an earlier run.
/// </remarks>
[TestFixture]
public class FeatureFlagAdministrationTests : TestBase
{
    private IFeatureFlagGrain Flags => GetGrainFactory().GetGrain<IFeatureFlagGrain>(Guid.Empty);

    private static string NewFlagId(string name) => $"cov.{name}.{Guid.NewGuid():N}";

    private static string NewUssdCode() => $"*{Random.Shared.Next(100_000_000, 999_999_999)}#";

    private static FeatureFlagInput Input(
        string flagId,
        bool defaultEnabled = false,
        int? rollout = null,
        string? variants = null,
        string? ussd = null,
        DateTimeOffset? expiresAt = null,
        string? description = null)
        => new(flagId, description, defaultEnabled, rollout, variants, ussd, expiresAt);

    private async Task<string> CreateAsync(FeatureFlagInput input)
    {
        var created = await Flags.CreateFlagAsync(input);

        Assert.That(created.Success, Is.True, created.Error);

        return input.FlagId;
    }

    /// <summary>Writes a flag row as it is, past the validation the grain would apply.</summary>
    private async Task InsertRawAsync(string flagId, string? variants, CancellationToken ct)
    {
        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        db.FeatureFlags.Add(new FeatureFlagEntity
        {
            Id             = flagId,
            DefaultEnabled = true,
            Variants       = variants,
            CreatedAt      = DateTimeOffset.UtcNow,
            UpdatedAt      = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(ct);
        await Flags.InvalidateCacheAsync();
    }

    // ── Creating, updating, deleting ─────────────────────────────────────────────────────────────

    [Test, CancelAfter(60_000)]
    public async Task Create_refuses_input_it_could_not_evaluate(CancellationToken ct = default)
    {
        var flagId = NewFlagId("invalid");

        var refused = new Dictionary<string, FeatureFlagInput>
        {
            ["an empty id"]              = Input("   "),
            ["a negative rollout"]       = Input(flagId, rollout: -1),
            ["a rollout above 100"]      = Input(flagId, rollout: 101),
            ["variants that are not JSON"] = Input(flagId, variants: "control=50"),
            ["an empty variant object"]  = Input(flagId, variants: "{}"),
            ["a JSON null for variants"] = Input(flagId, variants: "null")
        };

        foreach (var (why, input) in refused)
        {
            var result = await Flags.CreateFlagAsync(input);

            Assert.Multiple(() =>
            {
                Assert.That(result.Success, Is.False, $"a flag with {why} was accepted");
                Assert.That(result.Error, Is.Not.Null.And.Not.Empty, $"refusing {why} gave no reason");
            });
        }

        Assert.That(await Flags.GetFlagAsync(flagId), Is.Null, "a refused flag was stored anyway");
    }

    [Test, CancelAfter(60_000)]
    public async Task Create_refuses_a_taken_id_and_a_taken_ussd_code(CancellationToken ct = default)
    {
        var code  = NewUssdCode();
        var first = await CreateAsync(Input(NewFlagId("first"), ussd: code));

        var sameId   = await Flags.CreateFlagAsync(Input(first));
        var sameCode = await Flags.CreateFlagAsync(Input(NewFlagId("second"), ussd: code));

        Assert.Multiple(() =>
        {
            Assert.That(sameId.Success, Is.False);
            Assert.That(sameId.Error, Does.Contain("already exists"));

            // Two flags on one code would make a dialed code activate whichever the query found first.
            Assert.That(sameCode.Success, Is.False);
            Assert.That(sameCode.Error, Does.Contain("already in use"));
        });
    }

    [Test, CancelAfter(60_000)]
    public async Task Update_changes_the_flag_and_refuses_bad_input_an_unknown_flag_and_a_taken_code(CancellationToken ct = default)
    {
        var takenCode = NewUssdCode();
        await CreateAsync(Input(NewFlagId("code-holder"), ussd: takenCode));

        var flagId = await CreateAsync(Input(NewFlagId("updated")));

        var invalid  = await Flags.UpdateFlagAsync(Input(flagId, rollout: 150));
        var unknown  = await Flags.UpdateFlagAsync(Input(NewFlagId("missing")));
        var conflict = await Flags.UpdateFlagAsync(Input(flagId, ussd: takenCode));

        Assert.Multiple(() =>
        {
            Assert.That(invalid.Success, Is.False);
            Assert.That(unknown.Error, Does.Contain("not found"));
            Assert.That(conflict.Error, Does.Contain("already in use"));
        });

        var expiresAt = DateTimeOffset.UtcNow.AddDays(3);
        var ownCode   = NewUssdCode();

        var updated = await Flags.UpdateFlagAsync(Input(flagId, defaultEnabled: true, rollout: 100,
            variants: """{"a":1,"b":1}""", ussd: $"  {ownCode}  ", expiresAt: expiresAt, description: "now on"));

        Assert.That(updated.Success, Is.True, updated.Error);

        var details = await Flags.GetFlagAsync(flagId);

        Assert.Multiple(() =>
        {
            Assert.That(details!.Description, Is.EqualTo("now on"));
            Assert.That(details.DefaultEnabled, Is.True);
            Assert.That(details.RolloutPercentage, Is.EqualTo(100));
            Assert.That(details.UssdActivationCode, Is.EqualTo(ownCode), "the code is stored trimmed");
            Assert.That(details.ExpiresAt, Is.EqualTo(expiresAt).Within(TimeSpan.FromMilliseconds(1)));
        });

        var summary = (await Flags.ListFlagsAsync()).Single(flag => flag.Id == flagId);

        Assert.That(summary.HasVariants, Is.True);
    }

    [Test, CancelAfter(60_000)]
    public async Task Deleting_what_does_not_exist_fails(CancellationToken ct = default)
    {
        var flag     = await Flags.DeleteFlagAsync(NewFlagId("never-created"));
        var @override = await Flags.DeleteOverrideAsync(Guid.NewGuid());

        Assert.Multiple(() =>
        {
            Assert.That(flag.Success, Is.False);
            Assert.That(flag.Error, Does.Contain("not found"));
            Assert.That(@override.Success, Is.False);
            Assert.That(@override.Error, Is.EqualTo("Override not found"));
        });
    }

    /// <summary>
    /// A deleted flag keeps its row. Creating one under the same id used to answer "already exists"
    /// for a flag the console no longer lists, could not update and could not delete — the id was
    /// gone for good. That is the one way a cosmetic kind is switched off (a flag named after it), so
    /// a kind switched back on by deleting its flag could never be switched off again.
    /// </summary>
    [Test, CancelAfter(60_000)]
    public async Task A_deleted_flag_can_be_created_again_and_starts_without_its_old_overrides(CancellationToken ct = default)
    {
        var flagId = await CreateAsync(Input(NewFlagId("recreated")));
        var userId = Guid.NewGuid();

        var set = await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.User, userId.ToString(), true, null, null));
        Assert.That(set.Success, Is.True, set.Error);

        Assert.That((await Flags.DeleteFlagAsync(flagId)).Success, Is.True);
        Assert.That(await Flags.GetFlagAsync(flagId), Is.Null);
        Assert.That((await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(userId))).IsEnabled, Is.False);

        var again = await Flags.CreateFlagAsync(Input(flagId, description: "second life"));

        Assert.That(again.Success, Is.True, again.Error);

        var details = await Flags.GetFlagAsync(flagId);

        Assert.Multiple(() =>
        {
            Assert.That(details, Is.Not.Null);
            Assert.That(details!.Description, Is.EqualTo("second life"));
            Assert.That(details.Overrides, Is.Empty, "the deleted flag's overrides came back with its id");
        });

        var evaluated = await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(userId));

        Assert.Multiple(() =>
        {
            Assert.That(evaluated.IsEnabled, Is.False);
            Assert.That(evaluated.ResolvedAt, Is.EqualTo(FeatureFlagScope.Global));
        });
    }

    // ── Overrides ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(60_000)]
    public async Task SetOverride_refuses_an_empty_target_a_bad_rollout_and_an_unknown_flag(CancellationToken ct = default)
    {
        var flagId = await CreateAsync(Input(NewFlagId("override-input")));

        var emptyTarget = await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.Country, "  ", true, null, null));
        var badRollout  = await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.Country, "DE", null, 101, null));
        var noFlag      = await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(NewFlagId("absent"), FeatureFlagScope.Country, "DE", true, null, null));

        Assert.Multiple(() =>
        {
            Assert.That(emptyTarget.Error, Is.EqualTo("Target id cannot be empty"));
            Assert.That(badRollout.Error, Does.Contain("between 0 and 100"));
            Assert.That(noFlag.Error, Does.Contain("not found"));
        });

        Assert.That((await Flags.GetFlagAsync(flagId))!.Overrides, Is.Empty);
    }

    [Test, CancelAfter(60_000)]
    public async Task Setting_an_override_twice_changes_the_one_override_in_place(CancellationToken ct = default)
    {
        var flagId = await CreateAsync(Input(NewFlagId("override-twice"), variants: """{"a":1,"b":1}"""));
        var userId = Guid.NewGuid();

        await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.User, userId.ToString(), true, null, "b"));

        var on = await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(userId));

        Assert.Multiple(() =>
        {
            Assert.That(on.IsEnabled, Is.True);
            Assert.That(on.Variant, Is.EqualTo("b"));
        });

        var second = await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.User, userId.ToString(), false, 40, null));

        Assert.That(second.Success, Is.True, second.Error);

        var overrides = (await Flags.GetFlagAsync(flagId))!.Overrides;

        Assert.Multiple(() =>
        {
            Assert.That(overrides, Has.Count.EqualTo(1), "a second override was added beside the first");
            Assert.That(overrides[0].Enabled, Is.False);
            Assert.That(overrides[0].RolloutPercentage, Is.EqualTo(40));
            Assert.That(overrides[0].ForcedVariant, Is.Null);
        });

        var off = await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(userId));

        Assert.Multiple(() =>
        {
            Assert.That(off.IsEnabled, Is.False);
            Assert.That(off.ResolvedAt, Is.EqualTo(FeatureFlagScope.User));
        });
    }

    /// <summary>
    /// An override is soft-deleted, and the soft-delete query filter hid the deleted row from the
    /// lookup meant to revive it — so setting it again inserted a second row under the unique
    /// (flag, scope, target) key and the call failed with a constraint violation.
    /// </summary>
    [Test, CancelAfter(60_000)]
    public async Task An_override_deleted_and_set_again_comes_back(CancellationToken ct = default)
    {
        var flagId = await CreateAsync(Input(NewFlagId("override-revived")));

        await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.Country, "NL", true, null, null));

        var overrideId = (await Flags.GetFlagAsync(flagId))!.Overrides.Single().OverrideId;

        var deleted = await Flags.DeleteOverrideAsync(overrideId);

        Assert.Multiple(() =>
        {
            Assert.That(deleted.Success, Is.True);
            Assert.That(deleted.FlagId, Is.EqualTo(flagId));
        });

        Assert.That((await Flags.EvaluateAsync(flagId, new FeatureFlagEvaluationContext { CountryCode = "NL" })).IsEnabled, Is.False);

        var again = await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.Country, "NL", true, null, null));

        Assert.That(again.Success, Is.True, again.Error);
        Assert.That((await Flags.GetFlagAsync(flagId))!.Overrides.Select(o => o.OverrideId), Is.EqualTo(new[] { overrideId }));

        var evaluated = await Flags.EvaluateAsync(flagId, new FeatureFlagEvaluationContext { CountryCode = "nl" });

        Assert.Multiple(() =>
        {
            Assert.That(evaluated.IsEnabled, Is.True);
            Assert.That(evaluated.ResolvedAt, Is.EqualTo(FeatureFlagScope.Country));
        });
    }

    // ── USSD activation ──────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(60_000)]
    public async Task A_ussd_code_finds_its_flag_only_while_the_flag_lives(CancellationToken ct = default)
    {
        var code   = NewUssdCode();
        var flagId = await CreateAsync(Input(NewFlagId("ussd"), ussd: $"  {code} "));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await Flags.FindFlagIdByUssdCodeAsync($" {code}  "), Is.EqualTo(flagId), "a dialed code is matched trimmed");
            Assert.That(await Flags.FindFlagIdByUssdCodeAsync("   "), Is.Null);
            Assert.That(await Flags.FindFlagIdByUssdCodeAsync(""), Is.Null);
            Assert.That(await Flags.FindFlagIdByUssdCodeAsync(NewUssdCode()), Is.Null);
        });

        Assert.That((await Flags.DeleteFlagAsync(flagId)).Success, Is.True);
        Assert.That(await Flags.FindFlagIdByUssdCodeAsync(code), Is.Null, "a deleted flag still answered its code");

        // Deleting released the code, so another flag may claim it.
        var successor = await CreateAsync(Input(NewFlagId("ussd-successor"), ussd: code));

        Assert.That(await Flags.FindFlagIdByUssdCodeAsync(code), Is.EqualTo(successor));
    }

    [Test, CancelAfter(60_000)]
    public async Task Ussd_activation_turns_the_flag_on_for_that_user_alone(CancellationToken ct = default)
    {
        var flagId  = await CreateAsync(Input(NewFlagId("ussd-activate"), variants: """{"solo":1}"""));
        var dialer  = Guid.NewGuid();
        var another = Guid.NewGuid();

        var activated = await Flags.ActivateForUserAsync(dialer, flagId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(activated.IsEnabled, Is.True);
            Assert.That(activated.ResolvedAt, Is.EqualTo(FeatureFlagScope.User));
            Assert.That(activated.Variant, Is.EqualTo("solo"));

            Assert.That((await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(dialer))).IsEnabled, Is.True);
            Assert.That((await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(another))).IsEnabled, Is.False);
        });

        // Dialing again is the same answer, not a second override.
        var again = await Flags.ActivateForUserAsync(dialer, flagId);

        Assert.That(again.IsEnabled, Is.True);
        Assert.That((await Flags.GetFlagAsync(flagId))!.Overrides, Has.Count.EqualTo(1));
    }

    [Test, CancelAfter(60_000)]
    public async Task Ussd_activation_wins_over_an_operator_switching_the_user_off(CancellationToken ct = default)
    {
        var flagId = await CreateAsync(Input(NewFlagId("ussd-over-operator"), defaultEnabled: true));
        var userId = Guid.NewGuid();

        await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.User, userId.ToString(), false, null, null));
        Assert.That((await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(userId))).IsEnabled, Is.False);

        var activated = await Flags.ActivateForUserAsync(userId, flagId);

        Assert.That(activated.IsEnabled, Is.True);
        Assert.That((await Flags.GetFlagAsync(flagId))!.Overrides.Single().Enabled, Is.True);
    }

    /// <summary>The same revival as <see cref="An_override_deleted_and_set_again_comes_back"/>, from the dialer's side.</summary>
    [Test, CancelAfter(60_000)]
    public async Task Ussd_activation_after_the_operator_deleted_the_override_turns_it_back_on(CancellationToken ct = default)
    {
        var flagId = await CreateAsync(Input(NewFlagId("ussd-revived")));
        var userId = Guid.NewGuid();

        await Flags.ActivateForUserAsync(userId, flagId);

        var overrideId = (await Flags.GetFlagAsync(flagId))!.Overrides.Single().OverrideId;
        await Flags.DeleteOverrideAsync(overrideId);

        Assert.That((await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(userId))).IsEnabled, Is.False);

        var again = await Flags.ActivateForUserAsync(userId, flagId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(again.IsEnabled, Is.True);
            Assert.That((await Flags.GetFlagAsync(flagId))!.Overrides.Select(o => o.OverrideId), Is.EqualTo(new[] { overrideId }));
        });
    }

    [Test, CancelAfter(60_000)]
    public async Task Ussd_activation_of_a_missing_or_expired_flag_changes_nothing(CancellationToken ct = default)
    {
        var userId  = Guid.NewGuid();
        var expired = await CreateAsync(Input(NewFlagId("ussd-expired"), expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)));

        var missing = await Flags.ActivateForUserAsync(userId, NewFlagId("ussd-missing"));
        var lapsed  = await Flags.ActivateForUserAsync(userId, expired);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(missing.IsEnabled, Is.False);
            Assert.That(lapsed.IsEnabled, Is.False);
            Assert.That((await Flags.GetFlagAsync(expired))!.Overrides, Is.Empty, "an expired flag still took an override");
        });
    }

    // ── Evaluation corners ───────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(60_000)]
    public async Task A_percentage_rollout_is_all_or_nothing_at_its_ends_and_stable_for_each_user(CancellationToken ct = default)
    {
        var none = await CreateAsync(Input(NewFlagId("rollout-0"), defaultEnabled: true, rollout: 0));
        var all  = await CreateAsync(Input(NewFlagId("rollout-100"), rollout: 100));
        var half = await CreateAsync(Input(NewFlagId("rollout-50"), defaultEnabled: true, rollout: 50));

        var users = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToList();

        var halfFirst  = new List<bool>();
        var halfSecond = new List<bool>();

        foreach (var user in users)
        {
            var context = FeatureFlagEvaluationContext.ForUser(user);
            var results = await Flags.EvaluateManyAsync([none, all, half], context);

            Assert.That(results[none].IsEnabled, Is.False, "a 0% rollout let a user in");
            Assert.That(results[all].IsEnabled, Is.True, "a 100% rollout left a user out");

            halfFirst.Add(results[half].IsEnabled);
            halfSecond.Add((await Flags.EvaluateAsync(half, context)).IsEnabled);
        }

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(halfSecond, Is.EqualTo(halfFirst), "a user moved in or out of the rollout between two reads");
            Assert.That(halfFirst, Does.Contain(true).And.Contain(false), "fifty percent of forty users was everybody or nobody");

            // Without a user the bucket comes from the flag id alone, so every anonymous read agrees.
            var anonymous = await Flags.EvaluateAsync(half, FeatureFlagEvaluationContext.Empty);
            Assert.That((await Flags.EvaluateAsync(half, FeatureFlagEvaluationContext.Empty)).IsEnabled, Is.EqualTo(anonymous.IsEnabled));
        });
    }

    [Test, CancelAfter(60_000)]
    public async Task Forced_variants_follow_the_same_priority_as_the_switch(CancellationToken ct = default)
    {
        var flagId = await CreateAsync(Input(NewFlagId("forced"), defaultEnabled: true, variants: """{"x":1,"y":1}"""));

        await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.Country, "FR", null, null, "country-pick"));
        await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.Client, "desktop", null, null, "client-pick"));

        var both       = await Flags.EvaluateAsync(flagId, new FeatureFlagEvaluationContext { CountryCode = "FR", ClientId = "desktop" });
        var clientOnly = await Flags.EvaluateAsync(flagId, new FeatureFlagEvaluationContext { CountryCode = "IT", ClientId = "DESKTOP" });

        Assert.Multiple(() =>
        {
            // Neither override says on or off, so the switch is the global default's.
            Assert.That(both.ResolvedAt, Is.EqualTo(FeatureFlagScope.Global));
            Assert.That(both.Variant, Is.EqualTo("country-pick"));
            Assert.That(clientOnly.Variant, Is.EqualTo("client-pick"));
        });
    }

    [Test, CancelAfter(60_000)]
    public async Task A_variant_document_that_cannot_be_used_leaves_the_flag_on_without_a_variant(CancellationToken ct = default)
    {
        var empty     = NewFlagId("variants-empty");
        var malformed = NewFlagId("variants-malformed");

        await InsertRawAsync(empty, "{}", ct);
        await InsertRawAsync(malformed, "{not json", ct);

        var weightless = await CreateAsync(Input(NewFlagId("variants-weightless"), defaultEnabled: true, variants: """{"only":0,"never":0}"""));
        var anonymous  = await CreateAsync(Input(NewFlagId("variants-anonymous"), defaultEnabled: true, variants: """{"p":1,"q":1}"""));

        var user    = FeatureFlagEvaluationContext.ForUser(Guid.NewGuid());
        var results = await Flags.EvaluateManyAsync([empty, malformed, weightless], user);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Values.Select(r => r.IsEnabled), Is.All.True);
            Assert.That(results[empty].Variant, Is.Null);
            Assert.That(results[malformed].Variant, Is.Null);

            // Nothing carries any weight, so there is nothing to split: the first variant is everybody's.
            Assert.That(results[weightless].Variant, Is.EqualTo("only"));

            Assert.That((await Flags.EvaluateAsync(anonymous, FeatureFlagEvaluationContext.Empty)).Variant, Is.AnyOf("p", "q"));
        });
    }

    /// <summary>
    /// An override's <c>RolloutPercentage</c> used to be stored, range-checked and shown by the console
    /// and then dropped from the evaluation snapshot, so "half of this country" with <c>Enabled</c> left
    /// null was skipped and the global default answered.
    /// </summary>
    [Test, CancelAfter(60_000)]
    public async Task An_override_rollout_percentage_is_applied(CancellationToken ct = default)
    {
        var flagId = await CreateAsync(Input(NewFlagId("override-rollout")));

        await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.Country, "SE", null, 100, null));

        var swede = await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(Guid.NewGuid(), "SE", null));
        var dane  = await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(Guid.NewGuid(), "DK", null));

        Assert.Multiple(() =>
        {
            Assert.That(swede.IsEnabled, Is.True, "a 100% rollout override for SE left a Swedish user out");
            Assert.That(swede.ResolvedAt, Is.EqualTo(FeatureFlagScope.Country));
            Assert.That(dane.IsEnabled, Is.False);
            Assert.That(dane.ResolvedAt, Is.EqualTo(FeatureFlagScope.Global));
        });
    }

    [Test, CancelAfter(60_000)]
    public async Task An_override_switch_beats_its_own_percentage_and_a_percentage_beats_the_scope_below(CancellationToken ct = default)
    {
        var flagId = await CreateAsync(Input(NewFlagId("override-priority"), defaultEnabled: true));
        var userId = Guid.NewGuid();

        // Off outright, whatever the percentage beside it says.
        await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.Client, "desktop", false, 100, null));

        // Nobody in NO: a percentage at country scope decides before the client scope is asked.
        await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.Country, "NO", null, 0, null));

        // And this one user is in, above them both.
        await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.User, userId.ToString(), null, 100, null));

        var desktop   = await Flags.EvaluateAsync(flagId, new FeatureFlagEvaluationContext { ClientId = "desktop" });
        var norwegian = await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(Guid.NewGuid(), "NO", "desktop"));
        var chosen    = await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(userId, "NO", "desktop"));
        var anyoneElse = await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(Guid.NewGuid(), "FI", "web"));

        Assert.Multiple(() =>
        {
            Assert.That((desktop.IsEnabled, desktop.ResolvedAt), Is.EqualTo((false, FeatureFlagScope.Client)));
            Assert.That((norwegian.IsEnabled, norwegian.ResolvedAt), Is.EqualTo((false, FeatureFlagScope.Country)));
            Assert.That((chosen.IsEnabled, chosen.ResolvedAt), Is.EqualTo((true, FeatureFlagScope.User)));
            Assert.That((anyoneElse.IsEnabled, anyoneElse.ResolvedAt), Is.EqualTo((true, FeatureFlagScope.Global)));
        });
    }

    [Test, CancelAfter(60_000)]
    public async Task An_override_percentage_picks_the_same_users_as_the_flags_own_rollout(CancellationToken ct = default)
    {
        var flagId = await CreateAsync(Input(NewFlagId("same-buckets")));
        var users  = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToList();

        await Flags.SetOverrideAsync(new FeatureFlagOverrideInput(flagId, FeatureFlagScope.Country, "PL", null, 30, null));

        var viaOverride = new List<bool>();

        foreach (var user in users)
            viaOverride.Add((await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(user, "PL", null))).IsEnabled);

        // The same thirty percent, asked of the flag itself.
        Assert.That((await Flags.UpdateFlagAsync(Input(flagId, rollout: 30))).Success, Is.True);

        var viaFlag = new List<bool>();

        foreach (var user in users)
            viaFlag.Add((await Flags.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(user))).IsEnabled);

        Assert.Multiple(() =>
        {
            Assert.That(viaOverride, Is.EqualTo(viaFlag), "a user's bucket depends on which scope asked");
            Assert.That(viaOverride, Does.Contain(true).And.Contain(false), "thirty percent of forty users was everybody or nobody");
        });
    }

    /// <summary>
    /// A flag id whose SHA-256 begins <c>00 00 00 80</c>: read as a little-endian int that is
    /// <c>int.MinValue</c>, which <c>Math.Abs</c> cannot negate. Found by brute force, so every anonymous
    /// evaluation of a rollout on this id used to throw <c>OverflowException</c> out of the grain.
    /// </summary>
    private const string IntMinHashFlagId = "cov.int-min.11.147562903";

    [Test, CancelAfter(60_000)]
    public async Task A_flag_whose_bucket_hash_is_int_min_still_evaluates(CancellationToken ct = default)
    {
        var rollout = Input(IntMinHashFlagId, rollout: 49);

        // A fixed id, so a database kept from an earlier run already holds it.
        if (!(await Flags.CreateFlagAsync(rollout)).Success)
            Assert.That((await Flags.UpdateFlagAsync(rollout)).Success, Is.True);

        // |int.MinValue| is 2^31, and 2^31 % 100 is bucket 48.
        var in49 = await Flags.EvaluateAsync(IntMinHashFlagId, FeatureFlagEvaluationContext.Empty);

        Assert.That((await Flags.UpdateFlagAsync(Input(IntMinHashFlagId, rollout: 48))).Success, Is.True);

        var in48 = await Flags.EvaluateAsync(IntMinHashFlagId, FeatureFlagEvaluationContext.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(in49.IsEnabled, Is.True);
            Assert.That(in48.IsEnabled, Is.False);
        });
    }
}
