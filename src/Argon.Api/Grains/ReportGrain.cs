namespace Argon.Grains;

using Argon.Api.Features.CoreLogic.Social;
using Argon.Core.Entities.Data;
using Argon.Core.Features.Logic;
using Argon.Features.Cache;
using Argon.Features.Moderation;
using ArgonContracts;
using ConsoleContracts;
using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;

/// <summary>
/// The report system. See <c>docs/architecture/report-system.md</c> for the design and the threat
/// model; the short version is at the top of <see cref="ReportSystemOptions"/>.
/// </summary>
/// <remarks>
/// <para>Filing is the only path a user reaches, and it has one shape: everything the client did
/// right is acknowledged with a receipt, whether or not a report was recorded. The receipt is the
/// report's id when one was written and a fresh id when one was not, and nothing distinguishes
/// them. The errors returned are the two a client can act on — a target it should not have
/// offered, and the caller reporting themself — and "does not exist" and "not yours to see" are
/// the same error on purpose.</para>
///
/// <para>Nothing filed here changes a target. A report opens or joins a case, moves it in the
/// queue and may mark it urgent. What happens to the target is decided by an operator through
/// <see cref="ResolveCaseAsync"/>, and applied there.</para>
/// </remarks>
[StatelessWorker]
public class ReportGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IGrainFactory grainFactory,
    IOptions<ReportSystemOptions> reportOptions,
    IArgonCacheDatabase cache,
    HybridCache lockdownCache,
    ISystemNotificationService notifications,
    ILogger<IReportGrain> logger) : Grain, IReportGrain
{
    private ReportSystemOptions Cfg => reportOptions.Value;

    /// <summary>The answer for anything that was heard and not kept.</summary>
    private static SuccessSubmitReport Acknowledged() => new(ArgonId.New());

    // ── Filing ────────────────────────────────────────────────────────────────────────────────

    public async Task<ISubmitReportResult> SubmitReportAsync(CreateReportInput input, CancellationToken ct = default)
    {
        var reporterId = this.GetUserId();

        if (!Cfg.IsEnabled)
            return Acknowledged();

        if (ReportTargetRules.Check(input.target, reporterId) is { } shape)
            return new FailedSubmitReport(shape);

        if (!ReportValidation.IsValidReasonForCategory(input.category, input.reason))
            return new FailedSubmitReport(SubmitReportError.INVALID_TARGET);

        try
        {
            // Two attempts, for the one race worth handling: two first reporters opening the same
            // case at once. The second loses on the partial unique index and simply files again,
            // now against the case the first one made.
            for (var attempt = 0;; attempt++)
            {
                try
                {
                    return await FileAsync(reporterId, input, ct);
                }
                catch (DbUpdateException e) when (attempt == 0)
                {
                    logger.LogDebug(e, "Report from {ReporterId} lost a race opening its case; filing again", reporterId);
                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to file a report from {ReporterId}", reporterId);
            return Acknowledged();
        }
    }

    private async Task<ISubmitReportResult> FileAsync(Guid reporterId, CreateReportInput input, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await using var ctx = await context.CreateDbContextAsync(ct);

        var reporter = await ctx.Users.AsNoTracking()
           .Where(u => u.Id == reporterId)
           .Select(u => new { u.CreatedAt, u.LockdownReason, u.LockDownExpiration })
           .FirstOrDefaultAsync(ct);

        if (reporter is null)
            return Acknowledged();

        var accountAgeDays = (int)(now - reporter.CreatedAt).TotalDays;

        if (accountAgeDays < Cfg.Filing.MinAccountAgeDays)
            return Acknowledged();

        // An account under a critical lockdown is not a witness. Middle-severity lockdowns keep
        // the right to report: someone under investigation may still be the one being harassed.
        var lockdownLapsed = reporter.LockDownExpiration is { } lapse && lapse <= now;
        if (!lockdownLapsed && ReportActionPlanner.SeverityOf(reporter.LockdownReason) == LockdownSeverity.Critical)
            return Acknowledged();

        // Before the target is even looked up, so a flood of made-up ids costs one cache
        // increment each and never reaches the database.
        if (!await AllowAsync($"report:rl:{reporterId:N}:h", Cfg.Filing.MaxReportsPerHour, TimeSpan.FromHours(1), ct)
         || !await AllowAsync($"report:rl:{reporterId:N}:d", Cfg.Filing.MaxReportsPerDay, TimeSpan.FromDays(1), ct))
            return Acknowledged();

        var target = await ResolveTargetAsync(ctx, reporterId, input.target, now, ct);

        if (target is null)
            return new FailedSubmitReport(SubmitReportError.INVALID_TARGET);

        if (target.TargetUserId == reporterId)
            return new FailedSubmitReport(SubmitReportError.CANNOT_REPORT_SELF);

        if (!await AllowAsync($"report:rl:{reporterId:N}:t:{target.GroupKey}", Cfg.Filing.MaxReportsPerTargetPerDay, TimeSpan.FromDays(1), ct))
            return Acknowledged();

        var duplicateSince = now.AddHours(-Cfg.Filing.DuplicateWindowHours);
        var duplicate = await ctx.Reports.AnyAsync(r =>
            r.ReporterId == reporterId
         && r.Category == input.category
         && r.CreatedAt > duplicateSince
         && r.Case != null
         && r.Case.GroupKey == target.GroupKey, ct);

        if (duplicate)
            return Acknowledged();

        var credibility = await ctx.UserTrustScores.AsNoTracking()
           .Where(t => t.UserId == reporterId)
           .Select(t => (int?)t.ReporterCredibility)
           .FirstOrDefaultAsync(ct) ?? Cfg.Priority.DefaultCredibility;

        int? targetTrust = target.TargetUserId is { } person
            ? await ctx.UserTrustScores.AsNoTracking()
               .Where(t => t.UserId == person)
               .Select(t => (int?)t.TrustScore)
               .FirstOrDefaultAsync(ct)
            : null;

        var pepper      = Cfg.Privacy.ReporterIdentityPepper;
        var addressHash = ReporterIdentityHasher.Hash(pepper, this.GetUserIp());
        var deviceHash  = ReporterIdentityHasher.Hash(pepper, RequestContext.Get("$caller_machine_id") as string);

        var @case     = await ctx.ReportCases.FirstOrDefaultAsync(c => c.GroupKey == target.GroupKey && c.IsOpen, ct);
        var isNewCase = @case is null;

        @case ??= new ReportCaseEntity
        {
            Id              = ArgonId.New(),
            GroupKey        = target.GroupKey,
            TargetKind      = target.Kind,
            TargetId        = target.TargetId,
            SpaceId         = target.SpaceId,
            ChannelId       = target.ChannelId,
            MessageId       = target.MessageId,
            ConversationId  = target.ConversationId,
            IsOpen          = true,
            Status          = ReportStatus.PENDING,
            TopCategory     = input.category,
            ContentSnapshot = target.SnapshotJson,
            FirstReportedAt = now,
            LastReportedAt  = now,
            AppliedAction   = ReportActionKind.NONE
        };

        if (isNewCase)
            ctx.ReportCases.Add(@case);

        var windowStart = now.AddMinutes(-Cfg.Escalation.WindowMinutes);
        var caseId      = @case.Id;

        var earlier = isNewCase
            ? []
            : (await ctx.Reports.AsNoTracking()
                 .Where(r => r.CaseId == caseId && r.CreatedAt > windowStart)
                 .Select(r => new
                  {
                      r.ReporterId, r.ReporterIpHash, r.ReporterDeviceHash,
                      r.ReporterAccountAgeDays, r.ReporterCredibilityAtTime, r.CreatedAt
                  })
                 .ToListAsync(ct))
              .Select(r => new ReporterSignal(r.ReporterId, r.ReporterIpHash, r.ReporterDeviceHash,
                   r.ReporterAccountAgeDays, r.ReporterCredibilityAtTime, r.CreatedAt))
              .ToList();

        var mine = new ReporterSignal(reporterId, addressHash, deviceHash, accountAgeDays, credibility, now);

        var independentBefore = ReportPolicy.CountIndependent(Cfg.Escalation, earlier, now);
        var independent       = ReportPolicy.CountIndependent(Cfg.Escalation, earlier.Append(mine), now);
        var bestCredibility   = earlier.Count == 0 ? credibility : Math.Max(credibility, earlier.Max(s => s.Credibility));
        var topCategory       = ReportPolicy.Higher(Cfg.Priority, @case.TopCategory, input.category);

        // Escalation is sticky: once a case is urgent it stays urgent until someone resolves it.
        var decision = @case.IsEscalated
            ? new EscalationDecision(true, @case.EscalationRule)
            : ReportPolicy.Evaluate(Cfg.Escalation, input.category, independent, credibility, targetTrust);

        if (decision.IsEscalated && !@case.IsEscalated)
        {
            @case.IsEscalated    = true;
            @case.EscalationRule = decision.Rule;

            if (@case.Status == ReportStatus.PENDING)
                @case.Status = ReportStatus.ESCALATED;
        }

        @case.ReportCount++;
        @case.IndependentReporterCount = independent;
        @case.TopCategory              = topCategory;
        @case.PriorityScore            = ReportPolicy.ComputePriority(Cfg.Priority, topCategory, bestCredibility, independent);
        @case.LastReportedAt           = now;

        var report = new ReportEntity
        {
            Id                        = ArgonId.New(),
            CaseId                    = @case.Id,
            ReporterId                = reporterId,
            TargetKind                = target.Kind,
            TargetId                  = target.TargetId,
            ChannelId                 = target.ChannelId,
            MessageId                 = target.MessageId is { } message ? (ulong)message : null,
            ConversationId            = target.ConversationId,
            Category                  = input.category,
            Reason                    = input.reason,
            AdditionalInfo            = TrimComment(input.additionalInfo),
            Status                    = @case.Status,
            AssignedOperatorId        = @case.AssignedOperatorId,
            ReporterCredibilityAtTime = credibility,
            ReporterIpHash            = addressHash,
            ReporterDeviceHash        = deviceHash,
            ReporterAccountAgeDays    = accountAgeDays,
            IsIndependent             = independent > independentBefore,
            PriorityScore             = ReportPolicy.ComputePriority(Cfg.Priority, input.category, credibility, 0),
            IsAutoEscalated           = decision.IsEscalated,
            EscalationRule            = decision.Rule
        };

        ctx.Reports.Add(report);
        await ctx.SaveChangesAsync(ct);

        return new SuccessSubmitReport(report.Id);
    }

    private string? TrimComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment) || Cfg.Filing.MaxAdditionalInfoLength == 0)
            return null;

        var trimmed = comment.Trim();

        return trimmed.Length <= Cfg.Filing.MaxAdditionalInfoLength
            ? trimmed
            : trimmed[..Cfg.Filing.MaxAdditionalInfoLength];
    }

    /// <summary>
    /// Sliding-window counter, the shape every other limiter in the product uses. Fails open: a
    /// cache incident must not turn into "nobody can report anything".
    /// </summary>
    private async Task<bool> AllowAsync(string key, int max, TimeSpan window, CancellationToken ct)
    {
        try
        {
            var count = await cache.StringIncrementAsync(key, ct);

            if (count == 1)
                await cache.KeyExpireAsync(key, window, ct);

            return count <= max;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Report rate-limit cache call failed; allowing (fail-open)");
            return true;
        }
    }

    public Task<List<ReportInfo>> GetMyReportsAsync(int limit, int offset, CancellationToken ct = default)
        => Task.FromResult(new List<ReportInfo>());

    // ── Target resolution ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a report is really about, after the database has been asked.
    /// </summary>
    /// <param name="TargetUserId">The person the case is against — the author, for a message.</param>
    private sealed record ResolvedTarget(
        ReportTargetKind Kind,
        Guid             TargetId,
        Guid?            TargetUserId,
        Guid?            SpaceId,
        Guid?            ChannelId,
        long?            MessageId,
        Guid?            ConversationId,
        string           GroupKey,
        string           SnapshotJson);

    /// <summary>
    /// Null for anything the reporter should not have been able to point at — a thing that does
    /// not exist and a thing they cannot see are the same null.
    /// </summary>
    private async Task<ResolvedTarget?> ResolveTargetAsync(ApplicationDbContext ctx, Guid reporterId, ReportTarget target, DateTimeOffset now, CancellationToken ct)
    {
        switch (ReportTargetRules.Canonical(target.kind))
        {
            case ReportTargetKind.USER:
                return await ResolveUserAsync(ctx, reporterId, target.targetId, now, ct);

            case ReportTargetKind.SPACE:
                return await ResolveSpaceAsync(ctx, reporterId, target.targetId, now, ct);

            case ReportTargetKind.CHANNEL:
                return await ResolveChannelAsync(ctx, reporterId, target.targetId, now, ct);

            case ReportTargetKind.MESSAGE:
                // Older clients send a direct message as MESSAGE with the peer where the channel
                // goes. A channel id that names no channel is that shape.
                return await ResolveSpaceMessageAsync(ctx, reporterId, target.channelId!.Value, (long)target.messageId!.Value, now, ct)
                    ?? await ResolveDirectMessageAsync(ctx, reporterId, target.channelId!.Value, (long)target.messageId!.Value, now, ct);

            case ReportTargetKind.DIRECT_MESSAGE:
                return await ResolveDirectMessageAsync(ctx, reporterId, target.targetId, (long)target.messageId!.Value, now, ct);

            default:
                return null;
        }
    }

    private static async Task<ResolvedTarget?> ResolveUserAsync(ApplicationDbContext ctx, Guid reporterId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var user = await ctx.Users.AsNoTracking()
           .Where(u => u.Id == userId)
           .Select(u => new { u.Username, u.DisplayName, u.AvatarFileId, u.CreatedAt })
           .FirstOrDefaultAsync(ct);

        if (user is null || userId == UserEntity.SystemUser || userId == UserEntity.EchoUser)
            return null;

        // The same anchors profile lookup needs. A bare user id is not enough to report a
        // stranger, for the same reason it is not enough to look one up.
        if (!await SocialReach.CanReachAsync(ctx, reporterId, userId, ct))
            return null;

        var profile = await ctx.UserProfiles.AsNoTracking()
           .Where(p => p.UserId == userId)
           .Select(p => new { p.Bio, p.CustomStatus })
           .FirstOrDefaultAsync(ct);

        var snapshot = new ReportContentSnapshot(
            "user", userId, profile?.CustomStatus, null, user.DisplayName, profile?.Bio, user.AvatarFileId,
            null, null, user.CreatedAt, now);

        return new ResolvedTarget(ReportTargetKind.USER, userId, userId, null, null, null, null,
            ReportTargetRules.GroupKey(ReportTargetKind.USER, userId, null, null, null),
            ReportSnapshots.Serialize(snapshot));
    }

    private static async Task<ResolvedTarget?> ResolveSpaceAsync(ApplicationDbContext ctx, Guid reporterId, Guid spaceId, DateTimeOffset now, CancellationToken ct)
    {
        var space = await ctx.Spaces.AsNoTracking()
           .Where(s => s.Id == spaceId)
           .Select(s => new { s.Name, s.Description, s.AvatarFileId, s.IsCommunity, s.CreatedAt })
           .FirstOrDefaultAsync(ct);

        if (space is null)
            return null;

        // A community is visible from its invite; a private space only from inside.
        if (!space.IsCommunity && !await IsMemberAsync(ctx, spaceId, reporterId, ct))
            return null;

        var snapshot = new ReportContentSnapshot(
            "space", null, null, null, space.Name, space.Description, space.AvatarFileId,
            spaceId, null, space.CreatedAt, now);

        return new ResolvedTarget(ReportTargetKind.SPACE, spaceId, null, spaceId, null, null, null,
            ReportTargetRules.GroupKey(ReportTargetKind.SPACE, spaceId, null, null, null),
            ReportSnapshots.Serialize(snapshot));
    }

    private static async Task<ResolvedTarget?> ResolveChannelAsync(ApplicationDbContext ctx, Guid reporterId, Guid channelId, DateTimeOffset now, CancellationToken ct)
    {
        var channel = await ctx.Channels.AsNoTracking()
           .Where(c => c.Id == channelId)
           .Select(c => new { c.SpaceId, c.Name, c.Description, c.CreatedAt })
           .FirstOrDefaultAsync(ct);

        if (channel is null || !await IsMemberAsync(ctx, channel.SpaceId, reporterId, ct))
            return null;

        var snapshot = new ReportContentSnapshot(
            "channel", null, null, null, channel.Name, channel.Description, null,
            channel.SpaceId, channelId, channel.CreatedAt, now);

        return new ResolvedTarget(ReportTargetKind.CHANNEL, channelId, null, channel.SpaceId, channelId, null, null,
            ReportTargetRules.GroupKey(ReportTargetKind.CHANNEL, channelId, null, null, null),
            ReportSnapshots.Serialize(snapshot));
    }

    private static async Task<ResolvedTarget?> ResolveSpaceMessageAsync(ApplicationDbContext ctx, Guid reporterId, Guid channelId, long messageId, DateTimeOffset now, CancellationToken ct)
    {
        var channel = await ctx.Channels.AsNoTracking()
           .Where(c => c.Id == channelId)
           .Select(c => new { c.SpaceId })
           .FirstOrDefaultAsync(ct);

        if (channel is null || !await IsMemberAsync(ctx, channel.SpaceId, reporterId, ct))
            return null;

        var message = await ctx.Messages.AsNoTracking()
           .FirstOrDefaultAsync(m => m.SpaceId == channel.SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted, ct);

        if (message is null)
            return null;

        var snapshot = new ReportContentSnapshot(
            "message", message.CreatorId, message.Text, ReportSnapshots.EntitiesJson(message.Entities), null, null, null,
            channel.SpaceId, channelId, message.CreatedAt, now);

        // The author is the case's target whatever id the client sent: the message is the fact.
        return new ResolvedTarget(ReportTargetKind.MESSAGE, message.CreatorId, message.CreatorId, channel.SpaceId, channelId, messageId, null,
            ReportTargetRules.GroupKey(ReportTargetKind.MESSAGE, message.CreatorId, channelId, null, messageId),
            ReportSnapshots.Serialize(snapshot));
    }

    private static async Task<ResolvedTarget?> ResolveDirectMessageAsync(ApplicationDbContext ctx, Guid reporterId, Guid peerId, long messageId, DateTimeOffset now, CancellationToken ct)
    {
        if (peerId == reporterId || peerId == Guid.Empty)
            return null;

        // The conversation is derived from the two participants, so a reporter can only ever
        // reach messages in a conversation they are part of — there is nothing to check.
        var conversationId = ConversationEntity.GenerateConversationId(reporterId, peerId);

        var message = await ctx.DirectMessages.AsNoTracking()
           .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.MessageId == messageId && !m.IsDeleted, ct);

        if (message is null || message.SenderId == UserEntity.SystemUser)
            return null;

        var snapshot = new ReportContentSnapshot(
            "direct-message", message.SenderId, message.Text, ReportSnapshots.EntitiesJson(message.Entities), null, null, null,
            null, null, message.CreatedAt, now);

        return new ResolvedTarget(ReportTargetKind.DIRECT_MESSAGE, message.SenderId, message.SenderId, null, null, messageId, conversationId,
            ReportTargetRules.GroupKey(ReportTargetKind.DIRECT_MESSAGE, message.SenderId, null, conversationId, messageId),
            ReportSnapshots.Serialize(snapshot));
    }

    private static Task<bool> IsMemberAsync(ApplicationDbContext ctx, Guid spaceId, Guid userId, CancellationToken ct)
        => ctx.UsersToServerRelations.AnyAsync(x => x.SpaceId == spaceId && x.UserId == userId, ct);

    // ── Reading, for the operator console ─────────────────────────────────────────────────────

    public async Task<ReportCasePage> GetCasesAsync(ReportCaseQuery query, CancellationToken ct = default)
    {
        var limit  = Math.Clamp(query.Limit, 1, Cfg.MaxPageSize);
        var offset = Math.Max(0, query.Offset);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var cases = ctx.ReportCases.AsNoTracking().AsQueryable();

        if (query.Status is { } status)
            cases = cases.Where(c => c.Status == status);
        if (query.Category is { } category)
            cases = cases.Where(c => c.TopCategory == category);

        var total = await cases.CountAsync(ct);

        var rows = await cases
           .OrderByDescending(c => c.IsOpen)
           .ThenByDescending(c => c.PriorityScore)
           .ThenByDescending(c => c.LastReportedAt)
           .Skip(offset)
           .Take(limit)
           .ToListAsync(ct);

        var names = await DisplayNamesAsync(ctx, rows.Select(c => (c.TargetKind, c.TargetId)), ct);

        return new ReportCasePage(rows.Select(c => Summarize(c, names)).ToList(), total, offset, limit);
    }

    public async Task<ReportCaseView?> GetCaseAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var @case = await ctx.ReportCases.AsNoTracking().FirstOrDefaultAsync(c => c.Id == caseId, ct);

        if (@case is null)
            return null;

        var reports = await ctx.Reports.AsNoTracking()
           .Include(r => r.Reporter)
           .Where(r => r.CaseId == caseId)
           .OrderBy(r => r.CreatedAt)
           .ToListAsync(ct);

        var names = await DisplayNamesAsync(ctx, [(@case.TargetKind, @case.TargetId)], ct);

        int? trust = ReportTargetRules.TargetsAPerson(@case.TargetKind)
            ? await ctx.UserTrustScores.AsNoTracking()
               .Where(t => t.UserId == @case.TargetId)
               .Select(t => (int?)t.TrustScore)
               .FirstOrDefaultAsync(ct)
            : null;

        return new ReportCaseView(
            Summarize(@case, names),
            @case.ContentSnapshot,
            reports.Select(r => View(r, names)).ToList(),
            @case.ResolutionNote,
            @case.ResolvedByOperatorId,
            trust);
    }

    public async Task<ReportPage> GetReportsAsync(ReportCaseQuery query, CancellationToken ct = default)
    {
        var limit  = Math.Clamp(query.Limit, 1, Cfg.MaxPageSize);
        var offset = Math.Max(0, query.Offset);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var reports = ctx.Reports.AsNoTracking().AsQueryable();

        if (query.Status is { } status)
            reports = reports.Where(r => r.Status == status);
        if (query.Category is { } category)
            reports = reports.Where(r => r.Category == category);

        var total = await reports.CountAsync(ct);

        var rows = await reports
           .OrderByDescending(r => r.PriorityScore)
           .ThenByDescending(r => r.CreatedAt)
           .Skip(offset)
           .Take(limit)
           .Include(r => r.Reporter)
           .ToListAsync(ct);

        var names = await DisplayNamesAsync(ctx, rows.Select(r => (r.TargetKind, r.TargetId)), ct);

        return new ReportPage(rows.Select(r => View(r, names)).ToList(), total, offset, limit);
    }

    public async Task<ReportEntryView?> GetReportAsync(Guid reportId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var report = await ctx.Reports.AsNoTracking()
           .Include(r => r.Reporter)
           .FirstOrDefaultAsync(r => r.Id == reportId, ct);

        if (report is null)
            return null;

        var names = await DisplayNamesAsync(ctx, [(report.TargetKind, report.TargetId)], ct);

        return View(report, names);
    }

    public async Task<Guid?> FindCaseByReportAsync(Guid reportId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        return await ctx.Reports.AsNoTracking()
           .Where(r => r.Id == reportId)
           .Select(r => r.CaseId)
           .FirstOrDefaultAsync(ct);
    }

    private static ReportCaseSummary Summarize(ReportCaseEntity c, IReadOnlyDictionary<Guid, string> names)
        => new(c.Id, c.TargetKind, c.TargetId, c.ChannelId, c.MessageId, names.GetValueOrDefault(c.TargetId, "unknown"),
            c.Status, c.TopCategory, c.PriorityScore, c.ReportCount, c.IndependentReporterCount, c.IsEscalated, c.EscalationRule,
            c.AssignedOperatorId, c.FirstReportedAt, c.LastReportedAt, c.ResolvedAt, c.AppliedAction);

    private static ReportEntryView View(ReportEntity r, IReadOnlyDictionary<Guid, string> names)
        => new(r.Id, r.ReporterId, r.Reporter.Username, r.TargetKind, r.TargetId, r.ChannelId,
            r.MessageId is { } message ? (long)message : null, names.GetValueOrDefault(r.TargetId, "unknown"),
            r.Category, r.Reason, r.AdditionalInfo, r.Status, r.AssignedOperatorId, r.ResolutionNote,
            r.CreatedAt, r.ResolvedAt, r.CaseId, r.PriorityScore, r.EscalationRule, r.IsIndependent);

    private static async Task<Dictionary<Guid, string>> DisplayNamesAsync(
        ApplicationDbContext ctx, IEnumerable<(ReportTargetKind Kind, Guid Id)> targets, CancellationToken ct)
    {
        var list  = targets.ToList();
        var names = new Dictionary<Guid, string>();

        var people   = list.Where(t => ReportTargetRules.TargetsAPerson(t.Kind)).Select(t => t.Id).Distinct().ToList();
        var spaces   = list.Where(t => t.Kind == ReportTargetKind.SPACE).Select(t => t.Id).Distinct().ToList();
        var channels = list.Where(t => t.Kind == ReportTargetKind.CHANNEL).Select(t => t.Id).Distinct().ToList();

        if (people.Count > 0)
            foreach (var user in await ctx.Users.AsNoTracking()
                        .Where(u => people.Contains(u.Id))
                        .Select(u => new { u.Id, u.Username, u.DisplayName })
                        .ToListAsync(ct))
                names[user.Id] = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;

        if (spaces.Count > 0)
            foreach (var space in await ctx.Spaces.AsNoTracking()
                        .Where(s => spaces.Contains(s.Id))
                        .Select(s => new { s.Id, s.Name })
                        .ToListAsync(ct))
                names[space.Id] = space.Name;

        if (channels.Count > 0)
            foreach (var channel in await ctx.Channels.AsNoTracking()
                        .Where(c => channels.Contains(c.Id))
                        .Select(c => new { c.Id, c.Name })
                        .ToListAsync(ct))
                names[channel.Id] = channel.Name;

        return names;
    }

    // ── Deciding ──────────────────────────────────────────────────────────────────────────────

    public async Task<ReportOperationResult> AssignCaseAsync(Guid caseId, Guid operatorId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var ctx = await context.CreateDbContextAsync(ct);

        var @case = await ctx.ReportCases.FirstOrDefaultAsync(c => c.Id == caseId, ct);

        if (@case is null)
            return ReportOperationResult.Fail("Case not found");

        if (!ReportCaseTransitions.CanAssign(@case.Status))
            return ReportOperationResult.Fail($"Case is {@case.Status} and cannot be assigned");

        @case.AssignedOperatorId = operatorId;
        @case.Status             = ReportStatus.UNDER_REVIEW;

        await ctx.Reports
           .Where(r => r.CaseId == caseId)
           .ExecuteUpdateAsync(s => s
               .SetProperty(r => r.Status, ReportStatus.UNDER_REVIEW)
               .SetProperty(r => r.AssignedOperatorId, operatorId)
               .SetProperty(r => r.UpdatedAt, now), ct);

        await ctx.SaveChangesAsync(ct);

        return ReportOperationResult.Ok;
    }

    public async Task<ReportOperationResult> ResolveCaseAsync(ResolveReportCaseCommand command, CancellationToken ct = default)
    {
        if (!ReportCaseTransitions.IsResolution(command.Status))
            return ReportOperationResult.Fail($"{command.Status} is not a resolution");

        if (!ReportActionPlanner.IsConsistent(command.Action, command.Status))
            return ReportOperationResult.Fail($"Action {command.Action} needs {ReportStatus.RESOLVED_ACTION_TAKEN}");

        var now = DateTimeOffset.UtcNow;

        await using var ctx = await context.CreateDbContextAsync(ct);

        var @case = await ctx.ReportCases.FirstOrDefaultAsync(c => c.Id == command.CaseId, ct);

        if (@case is null)
            return ReportOperationResult.Fail("Case not found");

        if (!ReportCaseTransitions.CanResolve(@case.Status, command.Status))
            return ReportOperationResult.Fail($"Case is already {@case.Status}");

        if (ReportActionPlanner.TargetsAPerson(command.Action) && !ReportTargetRules.TargetsAPerson(@case.TargetKind))
            return ReportOperationResult.Fail($"Action {command.Action} needs a case whose target is a person; this one is a {@case.TargetKind}");

        if (ReportActionPlanner.TargetsContent(command.Action) && !ReportTargetRules.CarriesContent(@case.TargetKind))
            return ReportOperationResult.Fail($"Action {command.Action} needs a case about a message; this one is a {@case.TargetKind}");

        // The action first: a case must not read "resolved, content deleted" over content that is
        // still there because the delete failed.
        var applied = await ApplyActionAsync(ctx, @case, command, now, ct);

        if (!applied.Success)
            return applied;

        var note = string.IsNullOrWhiteSpace(command.ResolutionNote) ? null : command.ResolutionNote.Trim();

        @case.Status               = command.Status;
        @case.IsOpen               = false;
        @case.ResolvedAt           = now;
        @case.ResolvedByOperatorId = command.OperatorId;
        @case.ResolutionNote       = note;
        @case.AppliedAction        = command.Action;

        await ctx.Reports
           .Where(r => r.CaseId == @case.Id)
           .ExecuteUpdateAsync(s => s
               .SetProperty(r => r.Status, command.Status)
               .SetProperty(r => r.ResolvedAt, now)
               .SetProperty(r => r.ResolvedByOperatorId, command.OperatorId)
               .SetProperty(r => r.ResolutionNote, note)
               .SetProperty(r => r.UpdatedAt, now), ct);

        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("Report case {CaseId} resolved as {Status} with {Action} by operator {OperatorId}",
            @case.Id, command.Status, command.Action, command.OperatorId);

        await AfterDecisionAsync(ctx, @case, command.Status, ct);

        return ReportOperationResult.Ok;
    }

    public async Task<ReportOperationResult> ReopenCaseAsync(Guid caseId, Guid operatorId, string? note, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var ctx = await context.CreateDbContextAsync(ct);

        var @case = await ctx.ReportCases.FirstOrDefaultAsync(c => c.Id == caseId, ct);

        if (@case is null)
            return ReportOperationResult.Fail("Case not found");

        if (!ReportCaseTransitions.CanReopen(@case.Status))
            return ReportOperationResult.Fail($"Case is {@case.Status} and cannot be reopened");

        var groupKey = @case.GroupKey;
        var newer    = await ctx.ReportCases.AsNoTracking()
           .Where(c => c.GroupKey == groupKey && c.IsOpen)
           .Select(c => (Guid?)c.Id)
           .FirstOrDefaultAsync(ct);

        if (newer is { } other)
            return ReportOperationResult.Fail($"A newer open case {other} exists for the same target; work that one");

        var status = ReportCaseTransitions.OpenStateFor(@case.IsEscalated);

        @case.IsOpen               = true;
        @case.Status               = status;
        @case.ResolvedAt           = null;
        @case.ResolvedByOperatorId = null;
        @case.AssignedOperatorId   = null;

        if (!string.IsNullOrWhiteSpace(note))
            @case.ResolutionNote = note.Trim();

        await ctx.Reports
           .Where(r => r.CaseId == caseId)
           .ExecuteUpdateAsync(s => s
               .SetProperty(r => r.Status, status)
               .SetProperty(r => r.ResolvedAt, (DateTimeOffset?)null)
               .SetProperty(r => r.ResolvedByOperatorId, (Guid?)null)
               .SetProperty(r => r.AssignedOperatorId, (Guid?)null)
               .SetProperty(r => r.UpdatedAt, now), ct);

        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
        {
            logger.LogWarning(e, "Reopening case {CaseId} collided with a case opened meanwhile", caseId);
            return ReportOperationResult.Fail("A case for the same target was opened meanwhile; work that one");
        }

        logger.LogInformation("Report case {CaseId} reopened by operator {OperatorId}", caseId, operatorId);

        // The lockdown an earlier resolution applied stays: lifting it is UnblockUser's job, and
        // an explicit one. Trust is recomputed because the confirmation this case counted for is
        // no longer a confirmation.
        if (ReportTargetRules.TargetsAPerson(@case.TargetKind))
            await RecalculateQuietlyAsync(@case.TargetId, ct);

        return ReportOperationResult.Ok;
    }

    private async Task<ReportOperationResult> ApplyActionAsync(ApplicationDbContext ctx, ReportCaseEntity @case, ResolveReportCaseCommand command, DateTimeOffset now, CancellationToken ct)
    {
        switch (command.Action)
        {
            case ReportActionKind.NONE:
                return ReportOperationResult.Ok;

            case ReportActionKind.WARN_USER:
                if (Cfg.Actions.NotifyTargetOnWarning)
                    await notifications.CreateAsync(@case.TargetId, "moderation.warning", @case.Id,
                        "A warning from moderation",
                        "Something you posted was reported and found to break the rules. Please review them; further violations may restrict your account.",
                        ct: ct);
                return ReportOperationResult.Ok;

            case ReportActionKind.MUTE_USER:
            case ReportActionKind.RESTRICT_USER:
            case ReportActionKind.BAN_USER:
                return await ApplyLockdownAsync(ctx, @case.TargetId, ReportActionPlanner.Lockdown(Cfg.Actions, command.Action)!, command.OperatorId, now, ct);

            case ReportActionKind.DELETE_CONTENT:
            case ReportActionKind.QUARANTINE_CONTENT:
                return await RemoveContentAsync(ctx, @case, command.OperatorId, now, ct);

            default:
                return ReportOperationResult.Fail($"Unknown action {command.Action}");
        }
    }

    private async Task<ReportOperationResult> ApplyLockdownAsync(ApplicationDbContext ctx, Guid userId, LockdownPlan plan, Guid operatorId, DateTimeOffset now, CancellationToken ct)
    {
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return ReportOperationResult.Fail("Target user not found");

        if (!ReportActionPlanner.ShouldApply(user.LockdownReason, user.LockDownExpiration, plan, now))
        {
            logger.LogInformation("Lockdown {Reason} for {UserId} not applied: a {Existing} until {Expiry} already outranks it",
                plan.Reason, userId, user.LockdownReason, user.LockDownExpiration);
            return ReportOperationResult.Ok;
        }

        user.LockdownReason       = plan.Reason;
        user.LockDownExpiration   = plan.Duration is { } duration ? now + duration : null;
        user.LockDownIsAppealable = plan.IsAppealable;

        await ctx.SaveChangesAsync(ct);
        await lockdownCache.RemoveAsync(ArgonRequestContext.LockdownCacheKey(userId), ct);

        logger.LogWarning("Lockdown {Reason} until {Expiry} applied to {UserId} by operator {OperatorId}",
            plan.Reason, user.LockDownExpiration, userId, operatorId);

        return ReportOperationResult.Ok;
    }

    private async Task<ReportOperationResult> RemoveContentAsync(ApplicationDbContext ctx, ReportCaseEntity @case, Guid operatorId, DateTimeOffset now, CancellationToken ct)
    {
        if (@case.MessageId is not { } messageId)
            return ReportOperationResult.Fail("Case carries no message");

        switch (@case.TargetKind)
        {
            case ReportTargetKind.MESSAGE when @case.ChannelId is { } channelId:
                // Through the channel, so the removal is broadcast like any other.
                await grainFactory.GetGrain<IChannelGrain>(channelId).DeleteMessageByModeration(messageId, operatorId, ct);
                return ReportOperationResult.Ok;

            case ReportTargetKind.DIRECT_MESSAGE when @case.ConversationId is { } conversationId:
                await ctx.DirectMessages
                   .Where(m => m.ConversationId == conversationId && m.MessageId == messageId && !m.IsDeleted)
                   .ExecuteUpdateAsync(s => s
                       .SetProperty(m => m.IsDeleted, true)
                       .SetProperty(m => m.DeletedAt, now)
                       .SetProperty(m => m.UpdatedAt, now), ct);
                return ReportOperationResult.Ok;

            default:
                return ReportOperationResult.Fail("Case carries no message");
        }
    }

    /// <summary>
    /// Everything that follows a decision and must not be able to undo it: trust on both sides,
    /// and the word to the people who reported.
    /// </summary>
    private async Task AfterDecisionAsync(ApplicationDbContext ctx, ReportCaseEntity @case, ReportStatus resolution, CancellationToken ct)
    {
        if (ReportTargetRules.TargetsAPerson(@case.TargetKind))
        {
            try
            {
                await grainFactory.GetGrain<IUserTrustGrain>(@case.TargetId).OnReportResolvedAsync(resolution, ct);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Trust recalculation for target {TargetId} failed after case {CaseId}", @case.TargetId, @case.Id);
            }
        }

        var caseId    = @case.Id;
        var reporters = await ctx.Reports.AsNoTracking()
           .Where(r => r.CaseId == caseId)
           .Select(r => r.ReporterId)
           .Distinct()
           .Take(200)
           .ToListAsync(ct);

        var (title, body) = resolution == ReportStatus.RESOLVED_ACTION_TAKEN
            ? ("Thanks for your report", "We reviewed what you reported and took action.")
            : ("Thanks for your report", "We reviewed what you reported and did not find a violation of our rules.");

        foreach (var reporterId in reporters)
        {
            await RecalculateQuietlyAsync(reporterId, ct);

            if (!Cfg.Actions.NotifyReporterOnResolution)
                continue;

            try
            {
                await notifications.CreateAsync(reporterId, "report.resolved", caseId, title, body, ct: ct);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not notify reporter {ReporterId} about case {CaseId}", reporterId, caseId);
            }
        }
    }

    private async Task RecalculateQuietlyAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await grainFactory.GetGrain<IUserTrustGrain>(userId).RecalculateTrustAsync(ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Trust recalculation for {UserId} failed", userId);
        }
    }
}
