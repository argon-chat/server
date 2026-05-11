namespace Argon.Grains;

using ArgonContracts;
using Argon.Features.Moderation;
using Core.Entities.Data;
using Orleans.Concurrency;

[StatelessWorker]
public class UserTrustGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IOptions<TrustScoringOptions> trustOptions,
    IOptions<ReportSystemOptions> reportOptions,
    ILogger<IUserTrustGrain> logger) : Grain, IUserTrustGrain
{
    private TrustScoringOptions Cfg => trustOptions.Value;
    private ReportSystemOptions RCfg => reportOptions.Value;

    public async Task<UserTrustInfo> GetTrustScoreAsync(CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        logger.LogInformation(
            "[TrustDiag] GetTrustScore for {UserId}: IsEnabled={IsEnabled}, DefaultTrustScore={DefaultTrustScore}, MaxTrustScore={MaxTrustScore}, CredBase={CredBase}",
            userId, RCfg.IsEnabled, Cfg.DefaultTrustScore, Cfg.MaxTrustScore, Cfg.CredibilityBase);

        if (!RCfg.IsEnabled)
        {
            logger.LogWarning("[TrustDiag] Report system DISABLED, returning default {DefaultTrustScore} for {UserId}", Cfg.DefaultTrustScore, userId);
            return new UserTrustInfo(userId, Cfg.DefaultTrustScore, 0, 0, 0, 0, DateTime.UtcNow);
        }

        await using var ctx = await context.CreateDbContextAsync(ct);

        var entity = await ctx.UserTrustScores.FindAsync([userId], ct);

        if (entity is null)
        {
            logger.LogInformation("[TrustDiag] No UserTrustScore entity for {UserId}, returning default {DefaultTrustScore}", userId, Cfg.DefaultTrustScore);
            return new UserTrustInfo(userId, Cfg.DefaultTrustScore, 0, 0, 0, 0, DateTime.UtcNow);
        }

        logger.LogInformation(
            "[TrustDiag] Found entity for {UserId}: TrustScore={TrustScore}, TotalReceived={TotalReceived}, Confirmed={Confirmed}, Filed={Filed}, False={False}",
            userId, entity.TrustScore, entity.TotalReportsReceived, entity.ConfirmedReportsReceived, entity.TotalReportsFiled, entity.FalseReportsFiled);

        return new UserTrustInfo(
            entity.UserId,
            entity.TrustScore,
            entity.TotalReportsReceived,
            entity.ConfirmedReportsReceived,
            entity.TotalReportsFiled,
            entity.FalseReportsFiled,
            entity.LastRecalculatedAt.UtcDateTime
        );
    }

    public async Task<UserTrustInfo> RecalculateTrustAsync(CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        if (!RCfg.IsEnabled)
            return new UserTrustInfo(userId, Cfg.DefaultTrustScore, 0, 0, 0, 0, DateTime.UtcNow);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var entity = await ctx.UserTrustScores.FindAsync([userId], ct);
        entity ??= await EnsureEntityAsync(ctx, userId, ct);

        var previousScore = entity.TrustScore;

        // ── Gather counters ──────────────────────────────────────────
        entity.TotalReportsReceived = await ctx.Reports
           .CountAsync(x => x.TargetId == userId, ct);

        entity.ConfirmedReportsReceived = await ctx.Reports
           .CountAsync(x => x.TargetId == userId
                && (x.Status == ReportStatus.RESOLVED_ACTION_TAKEN || x.Status == ReportStatus.ESCALATED), ct);

        entity.TotalReportsFiled = await ctx.Reports
           .CountAsync(x => x.ReporterId == userId, ct);

        entity.FalseReportsFiled = await ctx.Reports
           .CountAsync(x => x.ReporterId == userId
                && (x.Status == ReportStatus.DISMISSED || x.Status == ReportStatus.RESOLVED_NO_ACTION), ct);

        entity.UniqueReporterCount = await ctx.Reports
           .Where(x => x.TargetId == userId)
           .Select(x => x.ReporterId)
           .Distinct()
           .CountAsync(ct);

        entity.BlockedByCount = await ctx.UserBlocklist
           .CountAsync(x => x.BlockedId == userId, ct);

        entity.LastConfirmedReportAt = await ctx.Reports
           .Where(x => x.TargetId == userId
                && (x.Status == ReportStatus.RESOLVED_ACTION_TAKEN || x.Status == ReportStatus.ESCALATED))
           .OrderByDescending(x => x.ResolvedAt)
           .Select(x => x.ResolvedAt)
           .FirstOrDefaultAsync(ct);

        // ── Load confirmed reports with detail ───────────────────────
        var confirmedReports = await ctx.Reports
           .AsNoTracking()
           .Where(x => x.TargetId == userId
                && (x.Status == ReportStatus.RESOLVED_ACTION_TAKEN || x.Status == ReportStatus.ESCALATED))
           .Select(x => new
           {
               x.Category,
               x.ReporterCredibilityAtTime,
               ResolvedAt = x.ResolvedAt ?? x.CreatedAt
           })
           .ToListAsync(ct);

        // ── Multi-dimensional scoring ────────────────────────────────
        var contentScore    = 0.0;
        var socialScore     = 0.0;
        var commercialScore = 0.0;

        foreach (var r in confirmedReports)
        {
            var weight = Cfg.SeverityWeights.GetValueOrDefault(r.Category, Cfg.DefaultSeverityWeight);
            var credMultiplier = Math.Max(r.ReporterCredibilityAtTime, Cfg.MinCredibilityInImpact) / 100.0;
            var daysSince = (DateTimeOffset.UtcNow - r.ResolvedAt).TotalDays;
            var decay = CalculateDecayMultiplier(daysSince);
            var impact = weight * credMultiplier * decay;

            switch (ReportValidation.GetCategoryDimension(r.Category))
            {
                case ScoreDimension.Content:
                    contentScore += impact;
                    break;
                case ScoreDimension.Social:
                    socialScore += impact;
                    break;
                case ScoreDimension.Commercial:
                    commercialScore += impact;
                    break;
                case ScoreDimension.Nuisance:
                    socialScore += impact * Cfg.NuisanceToSocialFactor;
                    break;
            }
        }

        socialScore += Math.Min(entity.BlockedByCount * Cfg.BlockCountMultiplier, Cfg.BlockCountCap);

        entity.ContentViolationScore = (int)Math.Min(contentScore, Cfg.ContentScoreCap);
        entity.SocialBehaviorScore   = (int)Math.Min(socialScore, Cfg.SocialScoreCap);
        entity.CommercialAbuseScore  = (int)Math.Min(commercialScore, Cfg.CommercialScoreCap);

        // ── Positive signals ─────────────────────────────────────────
        entity.PositiveSignalScore = await CalculatePositiveSignalsAsync(ctx, userId, entity.LastConfirmedReportAt, ct);

        // ── Velocity penalty (unique-reporter-aware) ─────────────────
        var velocityWindow = DateTimeOffset.UtcNow.AddDays(-Cfg.VelocityWindowDays);
        var recentConfirmed = await ctx.Reports
           .CountAsync(x => x.TargetId == userId
                && (x.Status == ReportStatus.RESOLVED_ACTION_TAKEN || x.Status == ReportStatus.ESCALATED)
                && x.CreatedAt > velocityWindow, ct);

        var recentUniqueReporters = await ctx.Reports
           .Where(x => x.TargetId == userId
                && (x.Status == ReportStatus.RESOLVED_ACTION_TAKEN || x.Status == ReportStatus.ESCALATED)
                && x.CreatedAt > velocityWindow)
           .Select(x => x.ReporterId)
           .Distinct()
           .CountAsync(ct);

        var velocityPenalty = 0;
        if (recentConfirmed > Cfg.VelocityThreshold)
        {
            var excess = recentConfirmed - Cfg.VelocityThreshold;
            if (recentUniqueReporters >= Cfg.VelocityHighConfidenceReporters)
                velocityPenalty = excess * Cfg.VelocityHighConfidencePenalty;
            else if (recentUniqueReporters < Cfg.VelocityLowConfidenceReporters)
                velocityPenalty = excess * Cfg.VelocityLowConfidencePenalty;
            else
                velocityPenalty = excess * Cfg.VelocityMidPenalty;
        }

        // ── Score recovery bonus ─────────────────────────────────────
        var recoveryBonus = 0;
        if (entity.LastConfirmedReportAt.HasValue)
        {
            var daysSinceLastConfirmed = (DateTimeOffset.UtcNow - entity.LastConfirmedReportAt.Value).TotalDays;
            if (daysSinceLastConfirmed > Cfg.RecoveryStartDays)
                recoveryBonus = (int)Math.Min(daysSinceLastConfirmed - Cfg.RecoveryStartDays, Cfg.RecoveryMaxBonus);
        }
        else if (entity.TotalReportsReceived == 0)
        {
            recoveryBonus = Cfg.CleanRecordNeverReportedBonus;
        }

        // ── False report penalty ─────────────────────────────────────
        var falseReportPenalty = entity.FalseReportsFiled * Cfg.FalseReportPenalty;

        // ── Composite score ──────────────────────────────────────────
        var score = Cfg.MaxTrustScore
            - entity.ContentViolationScore
            - entity.SocialBehaviorScore
            - entity.CommercialAbuseScore
            + entity.PositiveSignalScore
            + recoveryBonus
            - velocityPenalty
            - falseReportPenalty;

        entity.TrustScore = Math.Clamp(score, Cfg.MinTrustScore, Cfg.MaxTrustScore);

        // ── Reporter credibility ─────────────────────────────────────
        entity.ReporterCredibility = await CalculateReporterCredibilityAsync(ctx, userId, ct);

        entity.LastRecalculatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt          = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync(ct);

        // ── Auto-actions at score thresholds ─────────────────────────
        await CheckAutoActionsAsync(ctx, userId, entity, previousScore, ct);

        return new UserTrustInfo(
            entity.UserId,
            entity.TrustScore,
            entity.TotalReportsReceived,
            entity.ConfirmedReportsReceived,
            entity.TotalReportsFiled,
            entity.FalseReportsFiled,
            entity.LastRecalculatedAt.UtcDateTime
        );
    }

    public async Task OnReportReceivedAsync(ReportCategory category, CancellationToken ct = default)
    {
        if (!RCfg.IsEnabled) return;

        var userId = this.GetPrimaryKey();

        try
        {
            await using var ctx = await context.CreateDbContextAsync(ct);
            var entity = await ctx.UserTrustScores.FindAsync([userId], ct);
            entity ??= await EnsureEntityAsync(ctx, userId, ct);

            entity.TotalReportsReceived++;

            var provisionalPenalty = Cfg.SeverityWeights.GetValueOrDefault(category, Cfg.DefaultSeverityWeight) / Cfg.ProvisionalPenaltyDivisor;
            entity.TrustScore = Math.Clamp(entity.TrustScore - provisionalPenalty, Cfg.MinTrustScore, Cfg.MaxTrustScore);
            entity.UpdatedAt  = DateTimeOffset.UtcNow;

            await ctx.SaveChangesAsync(ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to process report received for user {UserId}", userId);
        }
    }

    public async Task OnReportResolvedAsync(ReportStatus resolution, CancellationToken ct = default)
    {
        if (!RCfg.IsEnabled) return;

        await RecalculateTrustAsync(ct);
    }

    private double CalculateDecayMultiplier(double daysSinceResolved)
    {
        if (daysSinceResolved <= Cfg.DecayPhase1Days)
            return Math.Exp(-Cfg.DecayRate * daysSinceResolved);

        if (daysSinceResolved <= Cfg.DecayPhase2Days)
        {
            var baseMultiplier = Math.Exp(-Cfg.DecayRate * Cfg.DecayPhase1Days);
            return Math.Max(Cfg.DecayMinimum, baseMultiplier - (daysSinceResolved - Cfg.DecayPhase1Days) * Cfg.DecayPhase2Rate);
        }

        return Cfg.DecayMinimum;
    }

    private async Task<int> CalculatePositiveSignalsAsync(
        ApplicationDbContext ctx, Guid userId, DateTimeOffset? lastConfirmedReportAt, CancellationToken ct)
    {
        var boost = 0;

        var user = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return 0;

        var months = (DateTimeOffset.UtcNow - user.CreatedAt).TotalDays / 30.0;
        foreach (var tier in Cfg.AccountAgeTiers.OrderByDescending(t => t.MinMonths))
        {
            if (months >= tier.MinMonths)
            {
                boost += tier.Boost;
                break;
            }
        }

        if (!string.IsNullOrEmpty(user.PhoneNumber))
            boost += Cfg.PhoneVerifiedBoost;

        if (!string.IsNullOrEmpty(user.TotpSecret))
            boost += Cfg.TwoFactorBoost;

        if (user.HasActiveUltima)
            boost += Cfg.PremiumBoost;

        var friendCount = await ctx.Friends
           .CountAsync(f => f.UserId == userId, ct);
        boost += Math.Min(friendCount / Cfg.FriendBoostDivisor, Cfg.FriendBoostCap);

        if (lastConfirmedReportAt.HasValue)
        {
            var cleanDays = (DateTimeOffset.UtcNow - lastConfirmedReportAt.Value).TotalDays;
            foreach (var tier in Cfg.CleanRecordTiers.OrderByDescending(t => t.MinDays))
            {
                if (cleanDays >= tier.MinDays)
                {
                    boost += tier.Boost;
                    break;
                }
            }
        }

        return Math.Min(boost, Cfg.PositiveSignalCap);
    }

    private async Task<int> CalculateReporterCredibilityAsync(
        ApplicationDbContext ctx, Guid userId, CancellationToken ct)
    {
        var cred = Cfg.CredibilityBase;

        var confirmedFiled = await ctx.Reports
           .CountAsync(x => x.ReporterId == userId
                && (x.Status == ReportStatus.RESOLVED_ACTION_TAKEN || x.Status == ReportStatus.ESCALATED), ct);

        var dismissedFiled = await ctx.Reports
           .CountAsync(x => x.ReporterId == userId
                && (x.Status == ReportStatus.DISMISSED || x.Status == ReportStatus.RESOLVED_NO_ACTION), ct);

        var total = confirmedFiled + dismissedFiled;
        if (total > 0)
        {
            var accuracy = (double)confirmedFiled / total;
            cred += (int)(Cfg.CredibilityAccuracyMax * Math.Sqrt(accuracy));
        }

        var user = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is not null)
        {
            var monthsActive = (DateTimeOffset.UtcNow - user.CreatedAt).TotalDays / 30.0;
            cred += (int)Math.Min(Cfg.CredibilityAgeMax, monthsActive * Cfg.CredibilityAgeRate);
        }

        var confirmedReceived = await ctx.Reports
           .CountAsync(x => x.TargetId == userId
                && (x.Status == ReportStatus.RESOLVED_ACTION_TAKEN || x.Status == ReportStatus.ESCALATED), ct);

        if (confirmedReceived >= Cfg.CredibilitySelfReportedThreshold)
            cred -= Cfg.CredibilitySelfReportedPenalty;

        var rateAbuseWindow = DateTimeOffset.UtcNow.AddDays(-Cfg.CredibilityRateAbuseWindowDays);
        var recentFiled = await ctx.Reports
           .CountAsync(x => x.ReporterId == userId && x.CreatedAt > rateAbuseWindow, ct);

        if (recentFiled > Cfg.CredibilityRateAbuseThreshold)
            cred -= Cfg.CredibilityRateAbusePenalty;

        return Math.Clamp(cred, 0, 100);
    }

    private async Task CheckAutoActionsAsync(
        ApplicationDbContext ctx, Guid userId, UserTrustScoreEntity entity, int previousScore, CancellationToken ct)
    {
        try
        {
            var user = await ctx.Users.FindAsync([userId], ct);
            if (user is null || user.LockdownReason != LockdownReason.NONE) return;

            foreach (var threshold in Cfg.AutoActionThresholds.OrderBy(t => t.ScoreBelow))
            {
                if (entity.TrustScore < threshold.ScoreBelow && previousScore >= threshold.ScoreBelow)
                {
                    if (threshold.Reason is not null && threshold.LockdownDays > 0)
                    {
                        user.LockdownReason      = Enum.Parse<LockdownReason>(threshold.Reason);
                        user.LockDownExpiration   = DateTimeOffset.UtcNow.AddDays(threshold.LockdownDays);
                        user.LockDownIsAppealable = true;
                    }

                    entity.AutoActionsApplied++;
                    await ctx.SaveChangesAsync(ct);
                    logger.LogWarning("Auto-action triggered: user {UserId} trust score {Score} crossed threshold {Threshold}",
                        userId, entity.TrustScore, threshold.ScoreBelow);
                    break;
                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to apply auto-action for user {UserId}", userId);
        }
    }

    private async Task<UserTrustScoreEntity> EnsureEntityAsync(ApplicationDbContext ctx, Guid userId, CancellationToken ct)
    {
        var entity = new UserTrustScoreEntity
        {
            UserId              = userId,
            TrustScore          = Cfg.DefaultTrustScore,
            ReporterCredibility = RCfg.DefaultReporterCredibility,
            LastRecalculatedAt  = DateTimeOffset.UtcNow,
            CreatedAt           = DateTimeOffset.UtcNow,
            UpdatedAt           = DateTimeOffset.UtcNow,
        };

        try
        {
            ctx.UserTrustScores.Add(entity);
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            ctx.Entry(entity).State = EntityState.Detached;
            entity = await ctx.UserTrustScores.FindAsync([userId], ct)
                ?? throw new InvalidOperationException($"UserTrustScore for {userId} missing after conflict");
        }

        return entity;
    }
}
