namespace Argon.Grains;

using Argon.Features.Logic;
using Argon.Grains.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// The daily pass over dormant accounts. It proposes; it no longer decides.
/// </summary>
/// <remarks>
/// <para><b>What changed and why.</b> This grain used to call
/// <see cref="IAccountDeletionGrain.RequestAutoDeleteAsync"/> on everything it found, which armed a grace
/// period and then anonymised the account — an irreversible operation applied by a timer, with nobody in
/// the loop. The campaign found three ways that went wrong (CON-2: an account that turned auto-delete
/// <em>off</em> was erased sooner than one that left it at thirty-six months; CON-3: the sweep skipped the
/// lockdown and owns-a-space bars the interactive path refuses outright; CON-4: a cancellation from the
/// console was undone by the next pass), and the product decision that followed was not to make the timer
/// safer but to take the decision off it. The scan now writes candidates into
/// <see cref="IAccountDeletionQueueGrain"/> and an operator approves them from the admin console, which
/// calls the same guarded path a person's own request goes through.</para>
///
/// <para><b>Two filters, and why both are here rather than one.</b> The bars that actually stand between an
/// account and erasure live in <c>AccountDeletionGrain.BarredAsync</c> and are enforced at approval — this
/// scan cannot weaken them and does not try. What it does is keep candidates an operator could not approve
/// anyway out of the operator's inbox, and the reason that matters is specific: an account under lockdown
/// cannot sign in, so it is <em>guaranteed</em> to reach the inactivity threshold and would sit in the
/// queue for ever. The projection below therefore reads the same three facts the guard reads — a standing
/// lockdown, an active subscription, a space the account still owns — as part of the one query it already
/// runs. If the two ever disagree the approval is what decides, and it refuses.</para>
///
/// <para><b>The scan is still gated by <see cref="AccountDeletionOptions.AutoDeleteEnabled"/></b>, which
/// remains the master switch: it decides whether the reminder is registered at all.
/// <see cref="RunScanAsync"/> is the manual entry point — the admin/debug force, and what the integration
/// suite drives — and runs whatever the switch says, exactly as it did before.</para>
/// </remarks>
public class AutoDeleteSchedulerGrain(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IGrainFactory grainFactory,
    IOptions<AccountDeletionOptions> options,
    ILogger<AutoDeleteSchedulerGrain> logger)
    : Grain, IAutoDeleteSchedulerGrain, IRemindable
{
    private const string ReminderName = "auto-delete-scan";
    private const int BatchSize = 100;
    private const int DefaultAutoDeleteMonths = 12;

    /// <summary>
    /// How many candidates one pass hands the queue: the queue's own ceiling, applied on this side of the
    /// grain call — see <see cref="ScanAndQueueAsync"/> and <see cref="IAccountDeletionQueueGrain.MaxEntries"/>.
    /// </summary>
    private const int MaxProposals = IAccountDeletionQueueGrain.MaxEntries;

    /// <summary>
    /// How many candidates the collection loop carries, so that the cut can be made after the filter
    /// that only the deletion grain can answer.
    /// </summary>
    /// <remarks>
    /// <para>Defect F2. The ceiling used to be <see cref="MaxProposals"/> itself, applied while
    /// collecting — before the per-candidate <c>GetDeletionStatusAsync</c> pass that removes accounts
    /// already scheduled, already being erased, stranded, or inside their holder's own decline hold. So
    /// the accounts that filter drops still occupied the places: an operator who approves five hundred
    /// dormant accounts leaves five hundred rows that stay <c>!IsDeleted</c> for the whole grace period
    /// and that are, by construction, the longest-idle accounts in the deployment. Every following pass
    /// collected exactly those, dropped all of them, and handed <c>ReconcileAsync</c> an empty proposal
    /// set — which does not merely fail to refill the worklist, it retires every pending entry left in
    /// it. At shipped values that is a month of an empty queue per batch of approvals, and a year per
    /// account whose holder cancels their own deletion.</para>
    ///
    /// <para>Three times the ceiling rather than "collect everything and cut last", because the cut
    /// exists in the first place to bound this pass (defect R28): the collection list, the number of
    /// sequential grain calls the status filter makes, and the size of the message
    /// <see cref="IAccountDeletionQueueGrain.ReconcileAsync"/> travels in. Fifteen hundred is a bound
    /// that leaves headroom for two full queues' worth of squatters and still costs a bounded pass on a
    /// deployment with a million dormant rows. The residual — over fifteen hundred longest-idle
    /// accounts all under deletion at once — would need three times the queue's whole capacity to be
    /// blocked simultaneously; decline holds, which are the unbounded population, are skipped before
    /// the cut and never enter this budget at all.</para>
    /// </remarks>
    private const int CollectionCeiling = MaxProposals * 3;

    /// <summary>Days per month, as the threshold arithmetic has always counted them.</summary>
    private const double DaysPerMonth = 30.44;

    /// <summary>How long after the reminder is first armed the first pass runs.</summary>
    /// <remarks>
    /// Long enough that a silo coming up has finished joining and is not scanning the whole user table
    /// while the rest of the fleet is still rolling.
    /// </remarks>
    private static readonly TimeSpan FirstScanDelay = TimeSpan.FromMinutes(5);

    /// <summary>How often the scan runs once armed.</summary>
    private static readonly TimeSpan ScanPeriod = TimeSpan.FromHours(24);

    /// <summary>
    /// Arms the scan, once, for the life of the cluster.
    /// </summary>
    /// <remarks>
    /// <para><b>Registered whatever the switch says.</b> It used to be registered only when
    /// <see cref="AccountDeletionOptions.AutoDeleteEnabled"/> was on, which made arming the scan
    /// depend on <em>which silo this grain happened to activate on and what that silo's configuration
    /// said at the time</em>. The startup call runs on a silo that has just come up, but placement can
    /// route a singleton to any silo hosting it — during a rolling restart, that is routinely the
    /// outgoing pod, which is still carrying the configuration the release replaced. That is what
    /// happened in production the day the switch was first turned on: the new pod's startup call
    /// reached the pod it was replacing, that pod read a stale <c>false</c>, registered nothing and
    /// died thirty seconds later, and the scan never ran again. Nothing re-asks: a reminder is
    /// registered on activation or never.
    ///
    /// <para>So the reminder is now unconditional and the switch is read at each pass, in
    /// <see cref="ReceiveReminder"/>, on whatever silo serves the tick — by then the whole fleet is
    /// carrying the same configuration. Turning auto-delete on or off takes effect within a day and
    /// needs no restart at all.</para>
    ///
    /// <para><b>And only when it is not already there.</b> <c>RegisterOrUpdateReminder</c> resets the
    /// schedule, so re-registering on every activation pushed the next pass to
    /// <see cref="FirstScanDelay"/> — and since a tick activates the grain, and the grain is collected
    /// for idleness between ticks, each pass re-armed the next one five minutes out. A daily scan ran
    /// every five minutes. Reading the reminder first costs one store round trip per activation and
    /// leaves the schedule where it was.</para>
    /// </remarks>
    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (await this.GetReminder(ReminderName) is not null)
            return;

        await this.RegisterOrUpdateReminder(ReminderName, FirstScanDelay, ScanPeriod);

        logger.LogInformation(
            "Auto-delete scan armed: first pass in {Delay}, every {Period} after that; each pass reads " +
            "the {Switch} switch, which is currently {State}",
            FirstScanDelay, ScanPeriod, nameof(AccountDeletionOptions.AutoDeleteEnabled),
            options.Value.AutoDeleteEnabled ? "on" : "off");
    }

    public ValueTask EnsureSchedulerActiveAsync()
        => ValueTask.CompletedTask; // activation itself registers the reminder

    public async ValueTask RunScanAsync()
        => await ScanAndQueueAsync();

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReminderName)
            return;

        // Skipped rather than unregistered, which is the other half of "the switch is read at each
        // pass": a reminder that took itself away could only be brought back by another restart, and
        // the restart is exactly what this grain must stop depending on. A pass that does nothing is a
        // few bytes in the reminder table and one log line a day.
        if (!options.Value.AutoDeleteEnabled)
        {
            logger.LogInformation("Auto-delete is disabled; skipping this pass");
            return;
        }

        logger.LogInformation("Auto-delete reminder fired, starting scan");

        try
        {
            await ScanAndQueueAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-delete scan failed");
        }
    }

    /// <summary>
    /// One pass: find the dormant accounts nothing bars, and hand the queue the set it can hold.
    /// </summary>
    /// <remarks>
    /// <para>The candidate set is reconciled in one call rather than enqueued row by row, because the
    /// queue's other half is retirement: an entry disappears when its account stops being a candidate, and
    /// only a set can tell "no longer proposed" from "not proposed yet". That is what makes the queue
    /// self-clearing when somebody signs in again.</para>
    ///
    /// <para><b>Bounded before it leaves this grain, though.</b> Defect R28: the pass used to accumulate
    /// every dormant account into one unbounded list, make one <c>GetDeletionStatusAsync</c> call per
    /// entry in it, and put the whole thing into a single Orleans message — the cap lived on the far side
    /// of that message. On a deployment turning auto-delete on for the first time, that is hundreds of
    /// thousands of records: hours of sequential grain calls, an activation directory full of grains
    /// nobody asked about, and a message that may simply be refused for its size, after which
    /// <see cref="ReceiveReminder"/> logs the exception and the queue stays empty with no explanation.</para>
    ///
    /// <para>So the cut is made here, where the ordering the queue would have applied is already known:
    /// keep the longest-idle accounts, and only ask the deletion grain about those. The ordering is
    /// stable under new arrivals — idle time grows at the same rate for everybody, so an account that
    /// has just crossed its threshold always joins at the young end — which is what makes a capped
    /// proposal set behave like the top of the uncapped one rather than flapping. Two things can still
    /// displace an entry: an account shortening its own threshold, and a decline hold lapsing. The first
    /// is rare and self-correcting; the second is why the held accounts are read and skipped before the
    /// cut rather than after it.</para>
    ///
    /// <para><b>In two stages, and that is the point of <see cref="CollectionCeiling"/></b> (defect F2).
    /// Collection keeps a multiple of the ceiling; the pass that removes accounts the database cannot
    /// speak for — already scheduled, already erasing, stranded, or inside their own holder's decline
    /// hold — runs over that; and <see cref="MaxProposals"/> is applied to what survives, immediately
    /// before <see cref="IAccountDeletionQueueGrain.ReconcileAsync"/>. Cutting first meant the accounts
    /// that filter exists to remove spent the pass's whole budget and the queue was handed nothing,
    /// which retires the entries already in it: the worklist did not stall, it emptied.</para>
    /// </remarks>
    private async Task ScanAndQueueAsync()
    {
        var processedCount = 0;
        var candidates     = new List<AccountDeletionCandidate>();

        await using var ctx = await dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;

        var queue = grainFactory.GetGrain<IAccountDeletionQueueGrain>(IAccountDeletionQueueGrain.SingletonId);
        var held  = await ReadDeclineHoldsAsync(queue);

        var systemUser = UserEntity.SystemUser;
        var offset     = 0;
        bool hasMore;

        do
        {
            var page = await ctx.Users
                .AsNoTracking()
                // Bots and the platform account are not people and have no console to answer from, so
                // they are excluded here rather than merely barred at approval: an account that can
                // never be approved does not belong in an operator's inbox. The bot test is the
                // back-reference and not `u.BotEntityId`, because that column is only set on accounts
                // seeded with one — in production one bot in twenty-three carries it, and the sweep
                // proposed the one that did. Every bot's last activity is the day it was created (a bot
                // never signs in), so without this every bot on the platform reaches the threshold and
                // is proposed for erasure a year after it is made.
                .Where(u => !u.IsDeleted
                         && !u.HasActiveUltima
                         && u.Id != systemUser
                         && !ctx.BotEntities.Any(bot => bot.BotAsUserId == u.Id))
                .OrderBy(u => u.Id)
                .Skip(offset)
                .Take(BatchSize)
                .Select(u => new
                {
                    u.Id,
                    u.CreatedAt,
                    u.LockdownReason,
                    u.LockDownExpiration,

                    // Enabled is read alongside Months, and nullably, which is the whole of CON-2. Filtering
                    // the row out with `&& s.Enabled` collapsed "the account switched auto-delete off" and
                    // "the account never chose" into the same absent value, and the decision below then
                    // applied the twelve-month default to both — so turning the feature off made the
                    // account eligible sooner than leaving it at thirty-six months would have. Two scalar
                    // subqueries rather than one projected row: the pair is unique per user, and this is
                    // the shape the provider translates without argument.
                    AutoDeleteEnabled = ctx.AutoDeleteSettings
                        .Where(s => s.UserId == u.Id)
                        .Select(s => (bool?)s.Enabled)
                        .FirstOrDefault(),

                    AutoDeleteMonths = ctx.AutoDeleteSettings
                        .Where(s => s.UserId == u.Id)
                        .Select(s => s.Months)
                        .FirstOrDefault(),

                    OwnsSpace = ctx.Spaces.Any(s => s.CreatorId == u.Id && !s.IsDeleted),

                    LastLogin = ctx.DeviceHistories
                        .Where(d => d.UserId == u.Id)
                        .Max(d => (DateTimeOffset?)d.LastLoginTime)
                })
                .ToListAsync();

            hasMore = page.Count == BatchSize;
            offset += page.Count;

            foreach (var row in page)
            {
                processedCount++;

                // An operator's refusal, applied before the cut rather than after it — see the remarks.
                if (held.Contains(row.Id))
                    continue;

                // An account that said "never" is never proposed. There is no fallback to the platform
                // default here on purpose: falling back is what made the switch have one position.
                if (row.AutoDeleteEnabled is false)
                    continue;

                var chosen          = row.AutoDeleteEnabled is true && row.AutoDeleteMonths is > 0;
                var thresholdMonths = chosen ? row.AutoDeleteMonths!.Value : DefaultAutoDeleteMonths;

                var lastActivity = row.LastLogin ?? row.CreatedAt;

                if (now - lastActivity < TimeSpan.FromDays(thresholdMonths * DaysPerMonth))
                    continue;

                // The bars, read here so a candidate an operator could never approve never reaches them.
                // A lapsed timed lockdown does not bar anything — nothing clears the column when a timed
                // ban runs out, so reading the reason alone would exempt every account that was ever muted
                // for an hour from retention for the rest of time. Same rule the deletion grain applies.
                if (row.LockdownReason != LockdownReason.NONE
                 && (row.LockDownExpiration is not { } expiry || expiry > now))
                    continue;

                if (row.OwnsSpace)
                    continue;

                candidates.Add(new AccountDeletionCandidate
                {
                    UserId          = row.Id,
                    LastActivityAt  = lastActivity,
                    ThresholdMonths = thresholdMonths,
                    Reason          = chosen
                        ? AccountDeletionQueueReasons.InactivityChosen
                        : AccountDeletionQueueReasons.InactivityDefault
                });
            }

            // Trimmed per page rather than at the end, so the pass never holds more than one page beyond
            // the ceiling however many dormant accounts the deployment has. To the collection ceiling
            // and not to MaxProposals: the filter below is what decides which of these can actually be
            // proposed, and it cannot recover a candidate this loop has already thrown away.
            TrimToLongestIdle(candidates, CollectionCeiling);
        } while (hasMore);

        // The last filter is the one only the deletion grain knows: an account already scheduled, already
        // being erased, or one whose holder has answered the notice and said no (CON-4's DeclinedAt). It is
        // per-candidate rather than part of the query because none of it is in the database.
        var proposals = new List<AccountDeletionCandidate>(candidates.Count);

        foreach (var candidate in candidates)
        {
            try
            {
                var status = await grainFactory.GetGrain<IAccountDeletionGrain>(candidate.UserId).GetDeletionStatusAsync();

                if (status.Status is not AccountDeletionStatusKind.None)
                    continue;

                if (status.DeclinedAt is { } declinedAt && now - declinedAt < options.Value.DeclineHoldsFor)
                    continue;

                proposals.Add(candidate);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read the deletion status of user {UserId}; not proposing it", candidate.UserId);
            }
        }

        // The queue's own ceiling, applied to what survived every filter rather than to what the
        // database offered: this is the list that actually travels, and its length is what
        // IAccountDeletionQueueGrain.MaxEntries is a statement about.
        var collected = proposals.Count;

        TrimToLongestIdle(proposals, MaxProposals);

        logger.LogInformation(
            "Auto-delete scan collected {Candidates} candidates, of which {Proposable} are proposable and " +
            "{Proposed} fit the queue's ceiling",
            candidates.Count, collected, proposals.Count);

        var result = await queue.ReconcileAsync(proposals);

        logger.LogInformation(
            "Auto-delete scan completed: processed {Processed}, proposed {Proposed}, enqueued {Enqueued}, " +
            "retired {Retired}, held {Held}, queue length {Length}",
            processedCount, proposals.Count, result.Enqueued, result.Retired, result.Held, result.Length);
    }

    /// <summary>
    /// Keeps a candidate list at <paramref name="ceiling"/>, longest-idle first.
    /// </summary>
    /// <remarks>
    /// The same ordering <see cref="IAccountDeletionQueueGrain.ReconcileAsync"/> applies on the far side,
    /// so capping here changes which accounts travel and not which ones would survive: the account that
    /// has been silent longest is the one an operator should be given.
    ///
    /// <para>Called twice with different ceilings, which is the whole of defect F2's fix: once per page
    /// at <see cref="CollectionCeiling"/>, to bound the pass, and once at <see cref="MaxProposals"/>
    /// after the per-candidate deletion-status filter, to bound what the queue is handed. Applying only
    /// the second ceiling first let accounts already under deletion — which the filter then drops —
    /// consume every place the queue had.</para>
    /// </remarks>
    private static void TrimToLongestIdle(List<AccountDeletionCandidate> candidates, int ceiling)
    {
        if (candidates.Count <= ceiling)
            return;

        candidates.Sort(static (left, right) => left.LastActivityAt.CompareTo(right.LastActivityAt));
        candidates.RemoveRange(ceiling, candidates.Count - ceiling);
    }

    /// <summary>
    /// The accounts an operator has declined, so the pass does not spend its places on them.
    /// </summary>
    /// <remarks>
    /// Fail-open: the queue filters holds itself in any case, so a read that does not answer costs the
    /// cap-filling and nothing else — a held account may take one of this pass's places and be dropped on
    /// arrival. Refusing to scan at all would be a worse answer to a store blip.
    /// </remarks>
    private async Task<HashSet<Guid>> ReadDeclineHoldsAsync(IAccountDeletionQueueGrain queue)
    {
        try
        {
            return [.. await queue.GetDeclineHoldsAsync()];
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read the queue's decline holds; scanning without them");

            return [];
        }
    }
}
