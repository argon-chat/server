namespace Argon.Grains;

using ArgonContracts;
using Argon.Features.Moderation;
using Argon.Services.Ion;
using Core.Entities.Data;
using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;

[StatelessWorker]
public class ReportGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IGrainFactory grainFactory,
    IOptions<ReportSystemOptions> reportOptions,
    HybridCache lockdownCache,
    ILogger<IReportGrain> logger) : Grain, IReportGrain
{
    private ReportSystemOptions Cfg => reportOptions.Value;

    public async Task<ISubmitReportResult> SubmitReportAsync(CreateReportInput input, CancellationToken ct = default)
    {
        if (!Cfg.IsEnabled)
            return new SuccessSubmitReport(Guid.CreateVersion7());

        var reporterId = this.GetUserId();

        if (input.target.targetId == reporterId)
            return new FailedSubmitReport(SubmitReportError.CANNOT_REPORT_SELF);

        if (!ReportValidation.IsValidReasonForCategory(input.category, input.reason))
            return new FailedSubmitReport(SubmitReportError.INVALID_TARGET);

        try
        {
            await using var ctx = await context.CreateDbContextAsync(ct);

            var reporter = await ctx.Users.AsNoTracking()
               .FirstOrDefaultAsync(u => u.Id == reporterId, ct);

            if (reporter is null)
                return new SuccessSubmitReport(Guid.CreateVersion7());

            var accountAgeDays = (int)(DateTimeOffset.UtcNow - reporter.CreatedAt).TotalDays;
            if (accountAgeDays < Cfg.MinAccountAgeDays)
                return new SuccessSubmitReport(Guid.CreateVersion7());

            var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1);
            var recentCount = await ctx.Reports
               .CountAsync(x => x.ReporterId == reporterId && x.CreatedAt > oneHourAgo, ct);

            if (recentCount >= Cfg.MaxReportsPerHour)
                return new SuccessSubmitReport(Guid.CreateVersion7());

            var oneDayAgo = DateTimeOffset.UtcNow.AddDays(-1);
            var perTargetCount = await ctx.Reports
               .CountAsync(x => x.ReporterId == reporterId
                    && x.TargetId == input.target.targetId
                    && x.CreatedAt > oneDayAgo, ct);

            if (perTargetCount >= Cfg.MaxReportsPerTargetPerDay)
                return new SuccessSubmitReport(Guid.CreateVersion7());

            var isDuplicate = await ctx.Reports
               .AnyAsync(x => x.ReporterId == reporterId
                    && x.TargetId == input.target.targetId
                    && x.Category == input.category
                    && x.CreatedAt > oneDayAgo, ct);

            if (isDuplicate)
                return new SuccessSubmitReport(Guid.CreateVersion7());

            var reporterTrust = await ctx.UserTrustScores
               .AsNoTracking()
               .FirstOrDefaultAsync(t => t.UserId == reporterId, ct);
            var reporterCredibility = reporterTrust?.ReporterCredibility ?? Cfg.DefaultReporterCredibility;

            var ipRaw = this.GetUserIp();
            var ipHash = ipRaw is not null
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ipRaw))).ToLowerInvariant()
                : null;

            var priorityScore = ComputePriorityScore(input.category, reporterCredibility);

            var (isEscalated, escalationRule) = await EvaluateAutoEscalation(
                ctx, input.category, input.target.targetId, reporterCredibility, ct);

            var entity = new ReportEntity
            {
                Id                        = Guid.CreateVersion7(),
                ReporterId                = reporterId,
                TargetKind                = input.target.kind,
                TargetId                  = input.target.targetId,
                ChannelId                 = input.target.channelId,
                MessageId                 = input.target.messageId,
                Category                  = input.category,
                Reason                    = input.reason,
                AdditionalInfo            = input.additionalInfo,
                Status                    = isEscalated ? ReportStatus.ESCALATED : ReportStatus.PENDING,
                ReferenceReportId         = input.referenceReportId,
                ReporterCredibilityAtTime = reporterCredibility,
                ReporterIpHash            = ipHash,
                ReporterAccountAgeDays    = accountAgeDays,
                PriorityScore             = priorityScore,
                IsAutoEscalated           = isEscalated,
                EscalationRule            = escalationRule,
            };

            ctx.Reports.Add(entity);
            await ctx.SaveChangesAsync(ct);

            if (reporterCredibility > Cfg.MinCredibilityForTrustNotification)
            {
                try
                {
                    var trustGrain = grainFactory.GetGrain<IUserTrustGrain>(input.target.targetId);
                    await trustGrain.OnReportReceivedAsync(input.category, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to notify trust grain for target {TargetId}", input.target.targetId);
                }
            }

            if (escalationRule == "CLUSTER_ESCALATION")
            {
                if (input.target.messageId is not null && input.target.channelId is not null)
                {
                    try
                    {
                        var channel = await ctx.Channels
                           .AsNoTracking()
                           .FirstOrDefaultAsync(c => c.Id == input.target.channelId.Value, ct);

                        if (channel is not null)
                        {
                            var msg = await ctx.Messages
                               .FirstOrDefaultAsync(m => m.SpaceId == channel.SpaceId
                                    && m.ChannelId == input.target.channelId.Value
                                    && m.MessageId == (long)input.target.messageId.Value, ct);

                            if (msg is not null)
                            {
                                msg.Text      = "[Message hidden by moderation]";
                                msg.Entities  = [];
                                msg.Controls  = null;
                                msg.UpdatedAt = DateTimeOffset.UtcNow;
                                await ctx.SaveChangesAsync(ct);
                                logger.LogWarning(
                                    "Cluster escalation: hid message {MessageId} in channel {ChannelId}",
                                    input.target.messageId, input.target.channelId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to hide message {MessageId} on cluster escalation", input.target.messageId);
                    }
                }

                if (Cfg.CriticalCategories.Contains(input.category))
                {
                    try
                    {
                        var targetUser = await ctx.Users.FindAsync([input.target.targetId], ct);
                        if (targetUser is not null && targetUser.LockdownReason == LockdownReason.NONE)
                        {
                            targetUser.LockdownReason      = LockdownReason.UNDER_INVESTIGATION;
                            targetUser.LockDownExpiration   = DateTimeOffset.UtcNow.AddDays(Cfg.CriticalCategoryLockdownDays);
                            targetUser.LockDownIsAppealable = true;
                            await ctx.SaveChangesAsync(ct);
                            await lockdownCache.RemoveAsync(ArgonRequestContext.LockdownCacheKey(input.target.targetId), ct);
                            logger.LogWarning(
                                "Cluster escalation on critical category: auto-locked user {TargetId} for investigation",
                                input.target.targetId);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to auto-lock user {TargetId} on cluster escalation", input.target.targetId);
                    }
                }
            }

            return new SuccessSubmitReport(entity.Id);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to submit report from {ReporterId}", reporterId);
            return new SuccessSubmitReport(Guid.Empty);
        }
    }

    public async Task<List<ReportInfo>> GetMyReportsAsync(int limit, int offset, CancellationToken ct = default)
    {
        return [];
        // TODO - research about whether to give this information to the user or not, as it can be abused to enumerate reports and glean information about moderators, other users' reports, etc. If we do want to give users access to their own reports, we should probably add some more filtering (e.g. only allow fetching reports from the last 30 days, or only return a count of reports instead of details, etc.)
        /*
        if (!Cfg.IsEnabled)
            return [];

        var reporterId = this.GetUserId();
        await using var ctx = await context.CreateDbContextAsync(ct);

        var reports = await ctx.Reports
           .AsNoTracking()
           .Where(x => x.ReporterId == reporterId)
           .OrderByDescending(x => x.CreatedAt)
           .Skip(offset)
           .Take(Math.Min(limit, Cfg.MaxReportsPerPage))
           .ToListAsync(ct);

        return reports.Select(r => new ReportInfo(
            r.Id,
            r.ReporterId,
            new ReportTarget(r.TargetKind, r.TargetId, r.ChannelId, r.MessageId),
            r.Category,
            r.Reason,
            r.AdditionalInfo,
            r.Status,
            r.ReferenceReportId,
            r.CreatedAt.UtcDateTime
        )).ToList();
        */
    }

    private int ComputePriorityScore(ReportCategory category, int reporterCredibility)
    {
        var baseScore = Cfg.CategoryPriorityBase.GetValueOrDefault(category, Cfg.DefaultPriorityBase);
        var credibilityBoost = reporterCredibility * Cfg.CredibilityPriorityMultiplier;
        return baseScore + credibilityBoost;
    }

    private async Task<(bool isEscalated, string? rule)> EvaluateAutoEscalation(
        ApplicationDbContext ctx,
        ReportCategory category,
        Guid targetId,
        int reporterCredibility,
        CancellationToken ct)
    {
        if (Cfg.CriticalCategories.Contains(category))
            return (true, "CRITICAL_CATEGORY");

        var clusterWindow = DateTimeOffset.UtcNow.AddMinutes(-Cfg.ClusterEscalationWindowMinutes);
        var uniqueReportersInWindow = await ctx.Reports
           .Where(x => x.TargetId == targetId && x.CreatedAt > clusterWindow)
           .Select(x => x.ReporterId)
           .Distinct()
           .CountAsync(ct);

        if (uniqueReportersInWindow >= Cfg.ClusterEscalationThreshold)
            return (true, "CLUSTER_ESCALATION");

        if (reporterCredibility >= Cfg.HighCredibilityThreshold && Cfg.SeriousCategories.Contains(category))
            return (true, "HIGH_CRED_SERIOUS");

        var targetTrust = await ctx.UserTrustScores
           .AsNoTracking()
           .FirstOrDefaultAsync(t => t.UserId == targetId, ct);

        if (targetTrust is not null && targetTrust.TrustScore < Cfg.LowTrustTargetThreshold && Cfg.SeriousCategories.Contains(category))
            return (true, "LOW_TRUST_TARGET");

        return (false, null);
    }
}
