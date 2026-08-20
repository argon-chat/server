namespace ArgonSharedLogicTest;

using Argon.Entities;
using Argon.Features.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Which rows the PostgreSQL sweeper would delete, and how fast — decided without a database.
/// </summary>
/// <remarks>
/// <para>Everything under test here is a pure function of the EF model, which is the property the whole
/// design was arranged around: the statements delete rows and no <c>ALTER</c> puts them back, so it has
/// to be possible to change the predicate and find out in a few seconds whether it still selects what
/// it should. The integration suite proves the statements run; this proves they are the right
/// statements.</para>
///
/// <para>No database anywhere. The model is built against a connection string nothing dials — building
/// a model opens nothing — exactly as <see cref="SchemaTtlDesiredStateTests"/> does.</para>
/// </remarks>
[TestFixture]
public class TtlSweepTests
{
    /// <summary>A host nothing dials.</summary>
    private const string Unreachable = "Host=localhost;Database=ttl-sweep-tests";

    private static readonly TableRef Invites        = new("public", "Invites");
    private static readonly TableRef TeamInvites    = new("public", "TeamInvites");
    private static readonly TableRef FriendRequests = new("public", "user_friend_requests");

    private static ApplicationDbContext Context()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
           .UseNpgsql(Unreachable)
           .Options;

        return new ApplicationDbContext(options, Options.Create(new DatabaseRegionOptions
        {
            PrimaryRegion   = "ru-central",
            ReplicateRegion = []
        }));
    }

    private static IReadOnlyList<TtlSweepTarget> Targets(TtlSweepOptions? options = null)
    {
        using var context = Context();

        return TtlSweepTargets.Resolve(context.Model, options ?? TtlSweepOptions.Default);
    }

    private static TtlSweepTarget Target(TableRef table, TtlSweepOptions? options = null)
        => Targets(options).Single(target => target.Table == table);

    #region the real model

    /// <summary>
    /// The sweeper sees the same three tables the reconciler does, because it reads the same call.
    /// </summary>
    /// <remarks>
    /// The point of the whole exercise. One <c>WithTTL</c> is what makes CockroachDB delete a row and
    /// what makes this delete the same row; the moment the two engines are driven by two lists, somebody
    /// updates one of them. If this goes red because a fourth table was declared, that is the test
    /// working — a new <c>WithTTL</c> now enrols a table in a delete loop and should be looked at once.
    /// </remarks>
    [Test]
    public void Every_table_the_reconciler_declares_is_a_sweep_target()
        => Assert.That(Targets().Select(target => target.Table.Name), Is.EquivalentTo(new[]
        {
            "Invites",
            "TeamInvites",
            "user_friend_requests"
        }));

    /// <summary>An invite dies when its own expiry column is in the past, and not a moment before.</summary>
    /// <remarks>
    /// <c>&lt;</c> and <c>now()</c> together are the whole rule CockroachDB applies to
    /// <c>ttl_expiration_expression</c>, and they are the whole rule here. The column is re-delimited
    /// because the canonical form drops the quotes: unquoted, <c>ExpireAt</c> folds to <c>expireat</c>
    /// and addresses a column that does not exist.
    /// </remarks>
    [Test]
    public void An_invite_expires_on_its_own_expiry_column_compared_against_the_server_clock()
        => Assert.That(Target(Invites).Predicate, Is.EqualTo("\"ExpireAt\" < now()"));

    [Test]
    public void A_team_invite_expires_on_the_same_rule()
        => Assert.That(Target(TeamInvites).Predicate, Is.EqualTo("\"ExpireAt\" < now()"));

    /// <summary>
    /// A friend request expires on the deadline stored in the row, not on the moment it was made.
    /// </summary>
    /// <remarks>
    /// <para>This assertion is the repair of a live landmine, and it is worth knowing what it was.
    /// <c>FriendRequestEntity</c> carries two timestamps: <c>ExpiredAt</c>, set six months out by
    /// <c>FriendsGrain</c> and put through <c>AsTTlField</c> — a helper that exists for no other
    /// purpose than making a column usable as a TTL column — and <c>RequestedAt</c>, which is
    /// <c>HasDefaultValueSql("now()")</c> and <c>ValueGeneratedOnAdd</c>. The <c>WithTTL</c> call
    /// named <c>RequestedAt</c>.</para>
    ///
    /// <para>Under the rule both engines use — expired once the named column is in the past — that
    /// declaration made every friend request expired the instant it was written, and asked whatever
    /// applied the TTL to delete the whole table on its first run. Nothing had happened only because
    /// the clause is emitted from <c>CreateTableOperation</c> and the table already existed; a
    /// reconciler turning the TTL on, or a regenerated migration, would have been the trigger.</para>
    ///
    /// <para>So the assertion is on the predicate rather than on "is it swept": the column name is the
    /// whole of what was wrong, and a test that only checked sweepability would have been green before
    /// the repair too.</para>
    /// </remarks>
    [Test]
    public void A_friend_request_expires_on_its_stored_deadline()
    {
        var target = Target(FriendRequests);

        Assert.Multiple(() =>
        {
            Assert.That(target.IsSweepable, Is.True, target.Refusal ?? string.Empty);
            Assert.That(target.Predicate, Is.EqualTo("\"ExpiredAt\" < now()"));
        });
    }

    /// <summary>A refused target has no predicate, and asking for one is a bug rather than an empty string.</summary>
    /// <remarks>
    /// Throwing rather than returning something harmless-looking. A refusal that degrades into
    /// <c>WHERE  &lt; now()</c> or into a predicate over the wrong column is the failure this whole
    /// guard exists to prevent, and it must not be reachable by a caller that forgot to check
    /// <see cref="TtlSweepTarget.IsSweepable"/>.
    /// </remarks>
    [Test]
    public void A_refused_target_refuses_to_produce_a_predicate()
        => Assert.Throws<InvalidOperationException>(() => _ = TargetFor(builder =>
        {
            builder.Property(x => x.ExpireAt).ValueGeneratedOnAdd();
            builder.WithTTL(x => x.ExpireAt, CronValue.Daily);
        }).Predicate);

    #endregion

    #region statements

    /// <summary>The delete is batched by <c>ctid</c>, under a lock, and bounded.</summary>
    /// <remarks>
    /// Each clause is load-bearing and each is easy to drop while tidying. Without <c>FOR UPDATE</c> a
    /// concurrent update moves a row to a new <c>ctid</c> between the sub-select and the delete and the
    /// row is silently missed; without <c>SKIP LOCKED</c> the sweep blocks behind whoever is accepting
    /// the invite it is trying to remove; without <c>LIMIT</c> it is one unbounded <c>DELETE</c> over
    /// the whole backlog, which is precisely the lock-the-table outcome the batching exists to avoid.
    /// </remarks>
    [Test]
    public void The_delete_takes_one_bounded_locked_batch_at_a_time()
    {
        var sql = Target(Invites).DeleteBatchSql;

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.StartWith("DELETE FROM \"public\".\"Invites\""));
            Assert.That(sql, Does.Contain("WHERE ctid IN ("));
            Assert.That(sql, Does.Contain("\"ExpireAt\" < now()"));
            Assert.That(sql, Does.Contain("LIMIT 5000"));
            Assert.That(sql, Does.Contain("FOR UPDATE SKIP LOCKED"));
        });
    }

    /// <summary>
    /// The count stops one past the budget, so it can say "at least" without scanning a backlog.
    /// </summary>
    /// <remarks>
    /// One past, not exactly the budget: a count that came back equal to the cap is the one case where
    /// "there are N" and "there are at least N" differ, and a report that could not tell them apart
    /// would say a table is nearly clear on the pass where it is anything but.
    /// </remarks>
    [Test]
    public void The_count_is_capped_one_row_past_the_budget()
    {
        // A synthetic table rather than Invites: its declared batch of 5000 exceeds the budget under
        // test, and the budget floors at one batch — so the cap here would be 5001, which is correct
        // behaviour and a useless assertion.
        var options = TtlSweepOptions.Default with { RowBudgetPerTable = 1_000, DefaultBatchSize = 100 };
        var target  = Sweepable(builder => builder.WithTTL(x => x.ExpireAt, CronValue.Daily), options);

        Assert.Multiple(() =>
        {
            Assert.That(target.RowBudget, Is.EqualTo(1_000));
            Assert.That(target.CountSql, Does.Contain("LIMIT 1001"));
        });
    }

    /// <summary>Both statements address the table the way the database spells it: quoted, mixed case.</summary>
    /// <remarks>
    /// <c>user_friend_requests</c> is the only one of the three whose name survives folding. The other
    /// two do not, and an unquoted <c>Invites</c> addresses <c>invites</c>, which does not exist — a
    /// sweep that deleted nothing forever and reported success.
    /// </remarks>
    [Test]
    public void Both_statements_delimit_the_table_name()
    {
        var target = Target(TeamInvites);

        Assert.Multiple(() =>
        {
            Assert.That(target.CountSql, Does.Contain("\"public\".\"TeamInvites\""));
            Assert.That(target.DeleteBatchSql, Does.Contain("\"public\".\"TeamInvites\""));
        });
    }

    #endregion

    #region batching and pacing

    /// <summary>A declared batch size is the batch size.</summary>
    [Test]
    public void A_declared_batch_size_is_used_verbatim()
        => Assert.That(Target(Invites).BatchSize, Is.EqualTo(5000));

    /// <summary>
    /// A table that declares no batch size gets the configured default, not a batch of zero.
    /// </summary>
    /// <remarks>
    /// <c>WithTTL</c>'s batch arguments default to <c>0</c> and zero means "no opinion" —
    /// <see cref="TtlSettings"/> normalises it to <c>null</c> for exactly that reason. A zero that
    /// reached <c>LIMIT</c> would produce a statement that deletes nothing and a loop that thinks it
    /// finished.
    /// </remarks>
    [Test]
    public void A_table_with_no_declared_batch_size_gets_the_configured_default()
    {
        var options = TtlSweepOptions.Default with { DefaultBatchSize = 250 };
        var target  = Sweepable(builder => builder.WithTTL(x => x.ExpireAt, CronValue.Daily), options);

        Assert.That(target.BatchSize, Is.EqualTo(250));
    }

    /// <summary>The budget is never smaller than one batch, whatever configuration says.</summary>
    /// <remarks>
    /// A budget below the batch size would let the loop delete one batch and immediately declare itself
    /// over budget, which is not wrong so much as it is a configuration that quietly means something
    /// other than what it says. Raising the budget rather than shrinking the batch keeps the declared
    /// batch — the thing whose size the author reasoned about — intact.
    /// </remarks>
    [Test]
    public void The_per_pass_budget_is_at_least_one_batch()
    {
        var options = TtlSweepOptions.Default with { RowBudgetPerTable = 10 };

        Assert.That(Target(Invites, options).RowBudget, Is.EqualTo(5000));
    }

    /// <summary>
    /// The declared rate limit is a rows-per-second figure, and the two invite tables declare one that
    /// means "no limit".
    /// </summary>
    /// <remarks>
    /// <c>52428800</c> is 50 MiB — a byte count sitting in <c>ttl_delete_rate_limit</c>, which
    /// CockroachDB documents in rows per second. Five thousand rows at fifty-two million a second owes a
    /// pause of about a tenth of a millisecond, so the floor is what actually paces the sweep. Delete
    /// the floor on the grounds that "the annotation already carries a rate limit" and the sweep becomes
    /// a tight loop of unbounded deletes against the primary.
    /// </remarks>
    [Test]
    public void An_absurd_rate_limit_falls_back_to_the_floor()
    {
        var options = TtlSweepOptions.Default with { MinimumBatchDelay = TimeSpan.FromMilliseconds(250) };

        Assert.That(Target(Invites, options).BatchDelay, Is.EqualTo(TimeSpan.FromMilliseconds(250)));
    }

    /// <summary>A rate limit that means something is honoured, and beats the floor.</summary>
    [Test]
    public void A_real_rate_limit_sets_the_pause_between_batches()
    {
        var options = TtlSweepOptions.Default with { MinimumBatchDelay = TimeSpan.FromMilliseconds(10) };

        // 100 rows a batch at 10 rows a second is ten seconds of pause owed per batch.
        var target = Sweepable(
            builder => builder.WithTTL(x => x.ExpireAt, CronValue.Daily, batchSize: 100, deleteRateLimit: 10),
            options);

        Assert.Multiple(() =>
        {
            Assert.That(target.BatchSize, Is.EqualTo(100));
            Assert.That(target.BatchDelay, Is.EqualTo(TimeSpan.FromSeconds(10)));
        });
    }

    /// <summary>A table with no rate limit declared still pauses.</summary>
    [Test]
    public void A_table_with_no_declared_rate_limit_still_pauses_for_the_floor()
    {
        var options = TtlSweepOptions.Default with { MinimumBatchDelay = TimeSpan.FromMilliseconds(75) };
        var target  = Sweepable(builder => builder.WithTTL(x => x.ExpireAt, CronValue.Daily), options);

        Assert.That(target.BatchDelay, Is.EqualTo(TimeSpan.FromMilliseconds(75)));
    }

    #endregion

    #region refusals

    /// <summary>An expiration column the store fills in is refused, whichever table it is on.</summary>
    /// <remarks>
    /// Asserted on a synthetic model as well as on the real one, so the rule survives the day somebody
    /// repairs <c>FriendRequestEntity</c>. It is a rule about declarations, not a special case for one
    /// table name.
    /// </remarks>
    [Test]
    public void A_store_generated_expiration_column_is_refused()
    {
        var target = TargetFor(builder =>
        {
            builder.Property(x => x.ExpireAt).ValueGeneratedOnAdd();
            builder.WithTTL(x => x.ExpireAt, CronValue.Daily);
        });

        Assert.Multiple(() =>
        {
            Assert.That(target.IsSweepable, Is.False);
            Assert.That(target.Refusal, Does.Contain("filled in by the database"));
        });
    }

    /// <summary>
    /// An expiration column whose name is not a plain identifier is refused rather than quoted and hoped for.
    /// </summary>
    /// <remarks>
    /// The column name is the one thing in this file that is concatenated into a <c>DELETE</c>. Every
    /// caller today is in this repository and every name is a plain identifier, which is exactly the
    /// condition under which such a check gets deleted as pointless. It is not pointless: the same
    /// <see cref="TtlSettings"/> type also carries expressions read back off a live server, and a
    /// future caller handing one of those to the sweeper has to fail closed.
    /// </remarks>
    [Test]
    public void An_expiration_column_that_is_not_a_plain_identifier_is_refused()
    {
        var target = TargetFor(builder =>
        {
            builder.Property(x => x.ExpireAt).HasColumnName("expires at").ValueGeneratedNever();
            builder.WithTTL(x => x.ExpireAt, CronValue.Daily);
        });

        Assert.Multiple(() =>
        {
            Assert.That(target.IsSweepable, Is.False);
            Assert.That(target.Refusal, Does.Contain("not a plain column name"));
        });
    }

    /// <summary>An ordinary application-written deadline column is swept.</summary>
    /// <remarks>
    /// The control case. Without it, every refusal above is equally consistent with a resolver that
    /// refuses everything, which would be a sweeper that reports diligently and deletes nothing.
    /// </remarks>
    [Test]
    public void An_application_written_deadline_column_is_swept()
    {
        var target = Sweepable(builder => builder.WithTTL(x => x.ExpireAt, CronValue.Daily));

        Assert.Multiple(() =>
        {
            Assert.That(target.IsSweepable, Is.True);
            Assert.That(target.Predicate, Is.EqualTo("\"ExpireAt\" < now()"));
            Assert.That(target.Table.Name, Is.EqualTo("perishables"));
        });
    }

    #endregion

    #region configuration

    /// <summary>
    /// Unset configuration means report, and so does a typo.
    /// </summary>
    /// <remarks>
    /// The single most important line in this fixture. A config map with <c>Mode: aply</c> must cost a
    /// log line saying how many rows would go, never an unattended <c>DELETE</c>. The engine key
    /// <c>Database:Provider</c> fails open in the other direction, and that asymmetry is deliberate:
    /// it only chooses a SQL generator.
    /// </remarks>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("aply")]
    [TestCase("yes")]
    public void An_unset_or_misspelled_mode_reports_rather_than_deletes(string? configured)
        => Assert.That(Configured(TtlSweepOptions.ModeKey, configured).Mode, Is.EqualTo(TtlSweepMode.Report));

    [Test]
    public void Deleting_is_opt_in_by_name()
        => Assert.That(Configured(TtlSweepOptions.ModeKey, "Apply").Mode, Is.EqualTo(TtlSweepMode.Apply));

    [Test]
    public void The_sweeper_can_be_turned_off_entirely()
        => Assert.That(Configured(TtlSweepOptions.ModeKey, "off").Mode, Is.EqualTo(TtlSweepMode.Off));

    /// <summary>An interval below Orleans' minimum reminder period is ignored, not clamped silently to zero.</summary>
    /// <remarks>
    /// A reminder registered with a period under a minute is rejected by Orleans at registration, which
    /// would take out the grain's activation rather than the setting. Falling back to the default keeps
    /// a bad number in configuration from becoming a role that will not start.
    /// </remarks>
    [TestCase("00:00:30")]
    [TestCase("nonsense")]
    public void An_impossible_interval_falls_back_to_the_default(string configured)
        => Assert.That(Configured(TtlSweepOptions.IntervalKey, configured).Interval,
            Is.EqualTo(TtlSweepOptions.Default.Interval));

    [Test]
    public void A_workable_interval_is_taken()
        => Assert.That(Configured(TtlSweepOptions.IntervalKey, "06:00:00").Interval,
            Is.EqualTo(TimeSpan.FromHours(6)));

    private static TtlSweepOptions Configured(string key, string? value)
        => TtlSweepOptions.FromConfiguration(new ConfigurationBuilder()
           .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
           .Build());

    #endregion

    #region a model of one's own

    private class Perishable
    {
        public Guid           Id       { get; set; }
        public DateTimeOffset ExpireAt { get; set; }
    }

    /// <summary>
    /// One model per configuration, because EF caches the built model against the context type.
    /// </summary>
    /// <remarks>
    /// Without this every test here would be handed whichever model was built first and would assert
    /// against a configuration it did not write — the trap <see cref="DbLocalityTests"/> documents and
    /// <see cref="SchemaTtlDesiredStateTests"/> works around the same way.
    /// </remarks>
    private sealed class PerConfigurationModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => (context.GetType(), ((PerishableContext)context).Configure, designTime);
    }

    private sealed class PerishableContext(
        DbContextOptions<PerishableContext> options,
        Action<ModelBuilder>                configure) : DbContext(options)
    {
        public Action<ModelBuilder> Configure { get; } = configure;

        protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder);
    }

    /// <summary>One synthetic table carrying whatever the case under test declares.</summary>
    private static TtlSweepTarget TargetFor(
        Action<EntityTypeBuilder<Perishable>> configure, TtlSweepOptions? options = null)
    {
        var builderOptions = new DbContextOptionsBuilder<PerishableContext>()
           .UseNpgsql(Unreachable);

        builderOptions.ReplaceService<IModelCacheKeyFactory, PerConfigurationModelCacheKeyFactory>();

        using var context = new PerishableContext(builderOptions.Options, modelBuilder =>
        {
            var entity = modelBuilder.Entity<Perishable>();

            entity.ToTable("perishables").HasKey(x => x.Id);

            configure(entity);
        });

        return TtlSweepTargets.Resolve(context.Model, options ?? TtlSweepOptions.Default).Single();
    }

    /// <summary>The same, with the assertion that it came out sweepable folded in.</summary>
    /// <remarks>
    /// So that a case about batching fails on the batching rather than on a
    /// <c>NullReferenceException</c> three lines later, and says which.
    /// </remarks>
    private static TtlSweepTarget Sweepable(
        Action<EntityTypeBuilder<Perishable>> configure, TtlSweepOptions? options = null)
    {
        var target = TargetFor(configure, options);

        Assert.That(target.IsSweepable, Is.True, target.Refusal ?? string.Empty);

        return target;
    }

    #endregion
}
