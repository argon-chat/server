namespace Argon.Api.Features.AdminApi;

using Argon.Api.Features.AdminApi.Diagnostics;
using Argon.Api.Grains.Interfaces;
using Argon.Core.Entities.Data;
using Argon.Core.Features.Logic;
using Argon.Features.Admin;
using Argon.Grains.Interfaces;
using ConsoleContracts;
using ion.runtime;
using Argon.Services.Ion;

/// <summary>
/// The operator console.
/// </summary>
/// <remarks>
/// <para><b>No database here, and none on the role that hosts it.</b> <c>admin</c> is a client role
/// and opens no connection of its own: every read and write goes through a grain — the four admin
/// grains (<see cref="IAdminUsersGrain"/>, <see cref="IAdminDirectoryGrain"/>,
/// <see cref="IAdminPlatformGrain"/>, <see cref="IAdminOperatorsGrain"/>) for what used to be queries
/// here, and the domain grains for everything that already had an owner. It is the arrangement the
/// account console has with <c>IDevTeamsGrain</c>, for the same reason: an Ion service holding a
/// connection pool is one the next person writes a query against.</para>
///
/// <para>What is left here is the mapping onto the console contract, the audit line, and the
/// operator's identity — which lives in an ambient context that does not cross a grain call, so every
/// grain method that needs it takes it as an argument.</para>
/// </remarks>
public class AdminConsoleImpl(
    IGrainFactory grainFactory,
    ILogger<IAdminConsole> logger,
    RuntimeDiagnosticsService runtimeDiagnostics,
    DatabaseDiagnosticsService databaseDiagnostics,
    KubernetesDiagnosticsService? kubernetesDiagnostics,
    NatsDiagnosticsService? natsDiagnostics,
    RedisDiagnosticsService? redisDiagnostics,
    OrleansDiagnosticsService? orleansDiagnostics,
    IOperatorAuditService auditService,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier sessionNotifier
) : IAdminConsole
{
    private IAdminUsersGrain     AdminUsers     => grainFactory.GetGrain<IAdminUsersGrain>(Guid.Empty);
    private IAdminDirectoryGrain AdminDirectory => grainFactory.GetGrain<IAdminDirectoryGrain>(Guid.Empty);
    private IAdminPlatformGrain  AdminPlatform  => grainFactory.GetGrain<IAdminPlatformGrain>(Guid.Empty);
    private IAdminOperatorsGrain AdminOperators => grainFactory.GetGrain<IAdminOperatorsGrain>(Guid.Empty);

    public Task<SearchUserResult> SearchUser(string query, CancellationToken ct = default)
        => AdminUsers.SearchUserAsync(query, ct);

    public async Task<UserCardDetails> GetUserCard(Guid userId, CancellationToken ct = default)
        => await AdminUsers.GetUserCardAsync(userId, ct)
        ?? throw new InvalidOperationException("User not found");

    public Task<PlatformStats> GetPlatformStats(CancellationToken ct = default)
        => AdminPlatform.GetPlatformStatsAsync(ct);

    public Task<ItemTemplateList> GetItemTemplates(CancellationToken ct = default)
        => AdminPlatform.GetItemTemplatesAsync(ct);

    public Task<DeleteItemResult> DeleteItemFromUserInventory(Guid userId, Guid itemId, CancellationToken ct = default)
        => AdminPlatform.DeleteItemFromUserInventoryAsync(userId, itemId, ct);

    public Task<DeleteItemResult> DeleteItemTemplate(Guid itemId, CancellationToken ct = default)
        => AdminPlatform.DeleteItemTemplateAsync(itemId, ct);

    public Task<CreateItemTemplateResult> CreateItemTemplate(CreateItemTemplateInput input, CancellationToken ct = default)
        => AdminPlatform.CreateItemTemplateAsync(input, ct);

    public Task<CouponList> GetCoupons(CancellationToken ct = default)
        => AdminPlatform.GetCouponsAsync(ct);

    public Task<CreateCouponResult> CreateCoupon(CreateCouponInput input, CancellationToken ct = default)
        => AdminPlatform.CreateCouponAsync(input, ct);

    public Task<UserActionResult> BlockUser(Guid userId, LockdownReason reason, DateTimeOffset? expiration, bool isAppealable,
        CancellationToken ct = default)
        => AuditedAsync(() => AdminUsers.SetLockdownAsync(userId, reason, expiration, isAppealable, ct),
            "BlockUser", "User", userId, $"Reason={reason}, expiration={expiration}, appealable={isAppealable}");

    public Task<UserActionResult> UnblockUser(Guid userId, CancellationToken ct = default)
        => AuditedAsync(() => AdminUsers.SetLockdownAsync(userId, LockdownReason.NONE, null, false, ct),
            "UnblockUser", "User", userId);

    public Task<DeviceList> GetUserDevices(Guid userId, CancellationToken ct = default)
        => AdminUsers.GetUserDevicesAsync(userId, ct);

    /// <summary>
    /// Everyone who has signed in from one machine.
    /// </summary>
    /// <remarks>
    /// The evidence a device ban is decided on, and the collateral it will cause. A shared family
    /// computer and an alt farm produce the same list, so this exists to be read by a person before
    /// <see cref="BanDevice"/> is used rather than to be counted by anything automatic.
    /// </remarks>
    public Task<DeviceAccountList> GetDeviceAccounts(Guid deviceId, CancellationToken ct = default)
        => AdminUsers.GetDeviceAccountsAsync(deviceId, ct);

    public Task<UserActionResult> BanDevice(Guid deviceId, string reason, DateTimeOffset? expiration, CancellationToken ct = default)
        => AuditedAsync(() => AdminUsers.BanDeviceAsync(deviceId, reason, expiration, ct),
            "BanDevice", "Device", deviceId, $"Reason={reason}, expiration={expiration}");

    public Task<UserActionResult> UnbanDevice(Guid deviceId, CancellationToken ct = default)
        => AuditedAsync(() => AdminUsers.UnbanDeviceAsync(deviceId, ct), "UnbanDevice", "Device", deviceId);

    public async Task<UserActionResult> GrantXp(Guid userId, int amount, CancellationToken ct = default)
    {
        try
        {
            if (!await AdminUsers.UserExistsAsync(userId, ct))
                return new UserActionResult(false, "User not found");

            var grain = grainFactory.GetGrain<IUserLevelGrain>(userId);
            await grain.AwardXpAsync(amount, XpSource.Event);

            await auditService.LogAsync("GrantXp", "User", userId.ToString(), $"Amount={amount}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> GrantItem(Guid userId, Guid itemId, CancellationToken ct = default)
    {
        try
        {
            if (!await AdminPlatform.ReferenceItemExistsAsync(itemId, ct))
                return new UserActionResult(false, "Reference item not found");

            var inventoryGrain = grainFactory.GetGrain<IInventoryGrain>(Guid.Empty);
            var success        = await inventoryGrain.GiveItemFor(userId, itemId, ct);

            if (!success)
                return new UserActionResult(false, "Failed to grant item");

            await auditService.LogAsync("GrantItem", "User", userId.ToString(), $"ItemId={itemId}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public Task<UserActionResult> ChangeUsername(Guid userId, string newUsername, CancellationToken ct = default)
        => AuditedAsync(() => AdminUsers.ChangeUsernameAsync(userId, newUsername, ct),
            "ChangeUsername", "User", userId, $"NewUsername={newUsername}");

    public Task<UserActionResult> RemoveTwoFactor(Guid userId, CancellationToken ct = default)
        => AuditedAsync(() => AdminUsers.RemoveTwoFactorAsync(userId, ct), "RemoveTwoFactor", "User", userId);

    public Task<UserActionResult> RemovePhoneNumber(Guid userId, CancellationToken ct = default)
        => AuditedAsync(() => AdminUsers.RemovePhoneNumberAsync(userId, ct), "RemovePhoneNumber", "User", userId);

    public Task<UserActionResult> ChangeEmail(Guid userId, string newEmail, CancellationToken ct = default)
        => AuditedAsync(() => AdminUsers.ChangeEmailAsync(userId, newEmail, ct),
            "ChangeEmail", "User", userId, $"NewEmail={newEmail}");

    public async Task<DiagnosticsResult> GetDiagnostics(CancellationToken ct = default)
    {
        var runtimeTask  = runtimeDiagnostics.GetDiagnosticsAsync(ct);
        var databaseTask = databaseDiagnostics?.GetDiagnosticsAsync(ct) ?? Task.FromResult<DatabaseDiagnostics?>(null);
        var k8sTask      = kubernetesDiagnostics?.GetDiagnosticsAsync(ct) ?? Task.FromResult<KubernetesDiagnostics?>(null);
        var natsTask     = natsDiagnostics?.GetDiagnosticsAsync(ct) ?? Task.FromResult<NatsDiagnostics?>(null);
        var redisTask    = redisDiagnostics?.GetDiagnosticsAsync(ct) ?? Task.FromResult<RedisDiagnostics?>(null);
        var orleansTask  = orleansDiagnostics?.GetDiagnosticsAsync(ct) ?? Task.FromResult<OrleansDiagnostics?>(null);

        await Task.WhenAll(runtimeTask, databaseTask, k8sTask, natsTask, redisTask, orleansTask);

        return new DiagnosticsResult(
            await runtimeTask,
            await databaseTask,
            await k8sTask,
            await natsTask,
            await redisTask,
            await orleansTask,
            DateTime.UtcNow
        );
    }

    public Task<OperatorList> GetOperators(CancellationToken ct = default)
        => AdminOperators.GetOperatorsAsync(ct);

    public async Task<OperatorDetails> GetOperatorDetails(Guid operatorId, CancellationToken ct = default)
        => await AdminOperators.GetOperatorDetailsAsync(operatorId, ct)
        ?? throw new InvalidOperationException("Operator not found");

    public async Task<CreateOperatorResult> CreateOperator(CreateOperatorInput input, CancellationToken ct = default)
    {
        try
        {
            var result = await AdminOperators.CreateOperatorAsync(CurrentOperatorId, input, ct);

            if (result.success)
                await auditService.LogAsync("CreateOperator", "Operator", result.operatorId.ToString(),
                    $"Created operator '{input.displayName.Trim()}' ({input.email.Trim().ToLowerInvariant()}), system={input.isSystemOperator}");

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create operator");
            return new CreateOperatorResult(false, null, "Failed to create operator");
        }
    }

    public async Task<OperatorActionResult> DeactivateOperator(Guid operatorId, CancellationToken ct = default)
    {
        try
        {
            var result = await AdminOperators.SetOperatorActiveAsync(CurrentOperatorId, operatorId, false, ct);

            if (result.success)
                await auditService.LogAsync("DeactivateOperator", "Operator", operatorId.ToString());

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deactivate operator={OperatorId}", operatorId);
            return new OperatorActionResult(false, "Failed to deactivate operator");
        }
    }

    public async Task<OperatorActionResult> ActivateOperator(Guid operatorId, CancellationToken ct = default)
    {
        try
        {
            var result = await AdminOperators.SetOperatorActiveAsync(CurrentOperatorId, operatorId, true, ct);

            if (result.success)
                await auditService.LogAsync("ActivateOperator", "Operator", operatorId.ToString());

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to activate operator={OperatorId}", operatorId);
            return new OperatorActionResult(false, "Failed to activate operator");
        }
    }

    public async Task<OperatorActionResult> RevokeOperatorCertificate(Guid certificateId, CancellationToken ct = default)
    {
        try
        {
            var revocation = await AdminOperators.RevokeCertificateAsync(CurrentOperatorId, certificateId, ct);

            if (revocation.Result.success)
                await auditService.LogAsync("RevokeOperatorCertificate", "Operator", revocation.OperatorId.ToString(),
                    $"Revoked certificate {certificateId} (serial={revocation.SerialNumber})");

            return revocation.Result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to revoke certificate={CertificateId}", certificateId);
            return new OperatorActionResult(false, "Failed to revoke certificate");
        }
    }

    public async Task<EnrollCertificateResult> EnrollOperatorCertificate(Guid operatorId, string csrPem, string? deviceName, string? deviceSerialNumber, CancellationToken ct = default)
    {
        var result = await AdminOperators.EnrollCertificateAsync(CurrentOperatorId, operatorId, csrPem, deviceName, deviceSerialNumber, ct);

        if (result.success)
            await auditService.LogAsync("EnrollOperatorCertificate", "Operator", operatorId.ToString(),
                $"Enrolled certificate serial={result.serialNumber}, device={result.thumbprint}");

        return result;
    }

    // ===== Operator App Access =====

    public Task<OperatorAppAccessList> GetOperatorAppAccess(Guid operatorId, CancellationToken ct = default)
        => AdminOperators.GetAppAccessAsync(operatorId, ct);

    public async Task<OperatorAppAccessResult> GrantOperatorAppAccess(GrantOperatorAppAccessInput input, CancellationToken ct = default)
    {
        try
        {
            var grant = await AdminOperators.GrantAppAccessAsync(CurrentOperatorId, input, ct);

            if (grant.Result.success)
                await auditService.LogAsync("GrantOperatorAppAccess", "OperatorAppAccess",
                    $"{input.operatorId}:{input.appId}",
                    $"Granted access to app '{grant.AppName}' (clientId={grant.ClientId}), scopes=[{string.Join(",", input.allowedScopes)}], claims=[{string.Join(",", input.claims)}]");

            return grant.Result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to grant operator app access");
            return new OperatorAppAccessResult(false, "Failed to grant operator app access");
        }
    }

    public async Task<OperatorActionResult> RevokeOperatorAppAccess(Guid operatorId, Guid appId, CancellationToken ct = default)
    {
        try
        {
            var result = await AdminOperators.RevokeAppAccessAsync(CurrentOperatorId, operatorId, appId, ct);

            if (result.success)
                await auditService.LogAsync("RevokeOperatorAppAccess", "OperatorAppAccess",
                    $"{operatorId}:{appId}", "Revoked app access");

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to revoke operator app access");
            return new OperatorActionResult(false, "Failed to revoke operator app access");
        }
    }

    public async Task<OperatorAppAccessResult> UpdateOperatorAppAccess(UpdateOperatorAppAccessInput input, CancellationToken ct = default)
    {
        try
        {
            var result = await AdminOperators.UpdateAppAccessAsync(CurrentOperatorId, input, ct);

            if (result.success)
                await auditService.LogAsync("UpdateOperatorAppAccess", "OperatorAppAccess",
                    $"{input.operatorId}:{input.appId}",
                    $"Updated: scopes=[{string.Join(",", input.allowedScopes)}], claims=[{string.Join(",", input.claims)}], active={input.isActive}");

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update operator app access");
            return new OperatorAppAccessResult(false, "Failed to update operator app access");
        }
    }

    public Task<InternalAppSearchResult> SearchInternalApps(string query, CancellationToken ct = default)
        => AdminDirectory.SearchInternalAppsAsync(query, ct);

    public Task<AuditLogPage> GetAuditLog(AuditLogQuery query, CancellationToken ct = default)
    {
        var page     = Math.Max(0, query.page);
        var pageSize = Math.Clamp(query.pageSize, 1, 100);

        return AdminOperators.QueryAuditAsync(
            query.operatorId, query.action, query.targetId,
            query.fromDate,
            query.toDate,
            page, pageSize, ct);
    }

    // ===== Bot Management =====

    public Task<AdminBotSearchResult> SearchBot(string query, CancellationToken ct = default)
        => AdminDirectory.SearchBotAsync(query, ct);

    public async Task<AdminBotCard> GetBotCard(Guid appId, CancellationToken ct = default)
        => await AdminDirectory.GetBotCardAsync(appId, ct)
        ?? throw new InvalidOperationException("Bot not found");

    public Task<UserActionResult> SetBotVerified(Guid appId, bool isVerified, CancellationToken ct = default)
        => AuditedAsync(() => AdminDirectory.SetBotVerifiedAsync(appId, isVerified, ct),
            "SetBotVerified", "Bot", appId, $"IsVerified={isVerified}");

    public Task<UserActionResult> SetBotMaxSpaces(Guid appId, int maxSpaces, CancellationToken ct = default)
        => AuditedAsync(() => AdminDirectory.SetBotMaxSpacesAsync(appId, maxSpaces, ct),
            "SetBotMaxSpaces", "Bot", appId, $"MaxSpaces={maxSpaces}");

    public Task<UserActionResult> SetBotInternalApp(Guid appId, bool isInternalApp, CancellationToken ct = default)
        => AuditedAsync(() => AdminDirectory.SetAppInternalAsync(appId, isInternalApp, ct),
            "SetBotInternalApp", "App", appId, $"IsInternalApp={isInternalApp}");

    public Task<UserActionResult> SetBotLifecycleState(Guid appId, AdminBotLifecycleState state, CancellationToken ct = default)
        => AuditedAsync(() => AdminDirectory.SetBotLifecycleStateAsync(appId, state, ct),
            "SetBotLifecycleState", "Bot", appId, $"State={state}");

    // ===== Team Management =====

    public Task<AdminTeamSearchResult> SearchTeam(string query, CancellationToken ct = default)
        => AdminDirectory.SearchTeamAsync(query, ct);

    public async Task<AdminTeamCard> GetTeamCard(Guid teamId, CancellationToken ct = default)
        => await AdminDirectory.GetTeamCardAsync(teamId, ct)
        ?? throw new InvalidOperationException("Team not found");

    // ===== Space Management =====

    public Task<AdminSpaceSearchResult> SearchSpace(string query, CancellationToken ct = default)
        => AdminDirectory.SearchSpaceAsync(query, ct);

    public async Task<AdminSpaceCard> GetSpaceCard(Guid spaceId, CancellationToken ct = default)
        => await AdminDirectory.GetSpaceCardAsync(spaceId, ct)
        ?? throw new InvalidOperationException("Space not found");

    public Task<UserActionResult> SetSpaceCommunity(Guid spaceId, bool isCommunity, CancellationToken ct = default)
        => SetSpaceFlag(spaceId, isCommunity: isCommunity, isOfficial: null,
            "SetSpaceCommunity", $"IsCommunity={isCommunity}", ct);

    public Task<UserActionResult> SetSpaceOfficial(Guid spaceId, bool isOfficial, CancellationToken ct = default)
        => SetSpaceFlag(spaceId, isCommunity: null, isOfficial: isOfficial,
            "SetSpaceOfficial", $"IsOfficial={isOfficial}", ct);

    /// <summary>
    /// Both space-flag buttons: check the space is really there, hand the flip to the grain (which
    /// owns the write, the cache drop and the broadcast), then audit it.
    /// </summary>
    private async Task<UserActionResult> SetSpaceFlag(Guid spaceId, bool? isCommunity, bool? isOfficial,
        string auditAction, string auditDetails, CancellationToken ct)
    {
        try
        {
            if (!await AdminDirectory.SpaceExistsAsync(spaceId, ct))
                return new UserActionResult(false, "Space not found");

            await grainFactory.GetGrain<ISpaceGrain>(spaceId).SetPlatformSpaceFlags(isCommunity, isOfficial, ct);
            await auditService.LogAsync(auditAction, "Space", spaceId.ToString(), auditDetails);
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    public Task<AdminSpaceMemberPage> GetSpaceMembers(Guid spaceId, int offset, int limit, CancellationToken ct = default)
        => AdminDirectory.GetSpaceMembersAsync(spaceId, offset, limit, ct);

    // ===== Premium Management =====

    public async Task<UserActionResult> CancelUserSubscription(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var grain  = grainFactory.GetGrain<IUltimaGrain>(userId);
            var result = await grain.CancelSubscriptionAsync(ct);
            if (!result)
                return new UserActionResult(false, "No active subscription to cancel");
            await auditService.LogAsync("CancelUserSubscription", "User", userId.ToString());
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    public async Task<UserActionResult> ExpireUserSubscription(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var grain = grainFactory.GetGrain<IUltimaGrain>(userId);
            await grain.ExpireSubscriptionAsync(ct);
            await auditService.LogAsync("ExpireUserSubscription", "User", userId.ToString());
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    public async Task<UserActionResult> GrantPremium(Guid userId, UltimaPlan tier, int durationDays, CancellationToken ct = default)
    {
        try
        {
            var grain = grainFactory.GetGrain<IUltimaGrain>(userId);
            await grain.ActivateSubscriptionAsync((UltimaTier)(int)tier, durationDays, null, null, ct);
            await auditService.LogAsync("GrantPremium", "User", userId.ToString(), $"Tier={tier}, Days={durationDays}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    // ===== Payment/Transaction API =====

    public Task<AdminTransactionPage> GetUserTransactions(Guid userId, int page, int pageSize, CancellationToken ct = default)
        => AdminUsers.GetUserTransactionsAsync(userId, page, pageSize, ct);

    public Task<AdminTransactionDetails?> GetTransactionByXsollaId(string xsollaTxId, CancellationToken ct = default)
        => AdminUsers.GetTransactionByXsollaIdAsync(xsollaTxId, ct);

    // ===== User Settings Mutations =====

    public Task<UserActionResult> ChangeUserAuthMode(Guid userId, ArgonAuthMode authMode, CancellationToken ct = default)
        => AuditedAsync(() => AdminUsers.ChangeAuthModeAsync(userId, authMode, ct),
            "ChangeUserAuthMode", "User", userId, $"AuthMode={authMode}");

    public Task<UserActionResult> ChangeUserOtpMethod(Guid userId, OtpMethod otpMethod, CancellationToken ct = default)
        => AuditedAsync(() => AdminUsers.ChangeOtpMethodAsync(userId, otpMethod, ct),
            "ChangeUserOtpMethod", "User", userId, $"OtpMethod={otpMethod}");

    public async Task<IUploadFileResult> BeginUploadUserAvatar(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var userGrain = grainFactory.GetGrain<IUserGrain>(userId);
            var result    = await userGrain.BeginUploadUserFile(UserFileKind.Avatar, ct);

            if (result.IsSuccess)
            {
                var t = result.Value;
                var formFields = new IonArray<FormField>(
                    t.Fields.Select(kv => new FormField(kv.Key, kv.Value)).ToList());
                return new SuccessUploadFile(t.BlobId, t.Url, formFields, t.TtlSeconds);
            }
            return new FailedUploadFile(result.Error);
        }
        catch (Exception)
        {
            return new FailedUploadFile(UploadFileError.INTERNAL_ERROR);
        }
    }

    public async Task<UserActionResult> CompleteUploadUserAvatar(Guid userId, Guid blobId, CancellationToken ct = default)
    {
        try
        {
            var userGrain = grainFactory.GetGrain<IUserGrain>(userId);
            await userGrain.CompleteUploadUserFile(blobId, UserFileKind.Avatar, ct);
            await auditService.LogAsync("ChangeUserAvatar", "User", userId.ToString());
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    // ===== Private Helpers =====

    /// <summary>
    /// One edit made by a grain and audited once it has been made.
    /// </summary>
    /// <remarks>
    /// The shape every simple account and bot button had when it wrote the row itself: a refusal from
    /// the grain comes back as the result and writes no audit line, and an exception anywhere — the
    /// grain call or the audit write — is the result too rather than a fault on the Ion call.
    /// </remarks>
    private async Task<UserActionResult> AuditedAsync(Func<Task<UserActionResult>> act, string action, string targetType,
        Guid targetId, string? details = null)
    {
        try
        {
            var result = await act();

            if (result.success)
                await auditService.LogAsync(action, targetType, targetId.ToString(), details);

            return result;
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    // ===== Reports & Trust =====
    //
    // The console does not read the report tables. Every call goes through IReportGrain, the one
    // place the report system's rules live; what is left here is the mapping onto the console
    // contract, the audit line, and the operator's id — which the grain cannot see for itself,
    // since an operator's identity lives in a context that does not cross a grain call.

    private IReportGrain Reports => grainFactory.GetGrain<IReportGrain>(Guid.CreateVersion7());

    private static Guid CurrentOperatorId => OperatorRequestContext.Current.OperatorId;

    public async Task<AdminReportPage> GetReports(ReportStatus? status, ReportCategory? category, int limit, int offset, CancellationToken ct = default)
    {
        var page = await Reports.GetReportsAsync(new ReportCaseQuery(status, category, limit, offset), ct);

        return new AdminReportPage(
            new IonArray<AdminReportEntry>(page.Reports.Select(ToEntry).ToList()),
            page.TotalCount,
            page.Offset,
            page.Limit);
    }

    public async Task<AdminReportEntry> GetReportById(Guid reportId, CancellationToken ct = default)
    {
        var report = await Reports.GetReportAsync(reportId, ct)
                  ?? throw new KeyNotFoundException($"Report {reportId} not found");

        return ToEntry(report);
    }

    /// <summary>Resolves the report's case, and with it every report on that case.</summary>
    public async Task<UserActionResult> ResolveReport(ResolveReportInput input, CancellationToken ct = default)
    {
        var caseId = await Reports.FindCaseByReportAsync(input.reportId, ct);

        if (caseId is null)
            return new UserActionResult(false, "Report not found");

        return await ResolveReportCase(new ResolveReportCaseInput(caseId.Value, input.status, input.resolutionNote, input.applyAction), ct);
    }

    public async Task<UserActionResult> AssignReport(Guid reportId, Guid operatorId, CancellationToken ct = default)
    {
        var caseId = await Reports.FindCaseByReportAsync(reportId, ct);

        if (caseId is null)
            return new UserActionResult(false, "Report not found");

        return await AssignReportCase(caseId.Value, operatorId, ct);
    }

    public async Task<AdminReportCasePage> GetReportCases(ReportStatus? status, ReportCategory? category, int limit, int offset, CancellationToken ct = default)
    {
        var page = await Reports.GetCasesAsync(new ReportCaseQuery(status, category, limit, offset), ct);

        return new AdminReportCasePage(
            new IonArray<AdminReportCaseSummary>(page.Cases.Select(ToSummary).ToList()),
            page.TotalCount,
            page.Offset,
            page.Limit);
    }

    public async Task<AdminReportCaseDetails> GetReportCase(Guid caseId, CancellationToken ct = default)
    {
        var view = await Reports.GetCaseAsync(caseId, ct)
                ?? throw new KeyNotFoundException($"Report case {caseId} not found");

        return new AdminReportCaseDetails(
            ToSummary(view.Summary),
            view.ContentSnapshot,
            new IonArray<AdminReportEntry>(view.Reports.Select(ToEntry).ToList()),
            view.ResolutionNote,
            view.ResolvedByOperatorId,
            view.TargetTrustScore);
    }

    public async Task<UserActionResult> AssignReportCase(Guid caseId, Guid operatorId, CancellationToken ct = default)
    {
        var result = await Reports.AssignCaseAsync(caseId, operatorId, ct);

        if (result.Success)
            await auditService.LogAsync("AssignReportCase", "ReportCase", caseId.ToString(), $"OperatorId={operatorId}");

        return new UserActionResult(result.Success, result.Error);
    }

    public async Task<UserActionResult> ResolveReportCase(ResolveReportCaseInput input, CancellationToken ct = default)
    {
        var result = await Reports.ResolveCaseAsync(
            new ResolveReportCaseCommand(input.caseId, input.status, input.resolutionNote, input.applyAction, CurrentOperatorId), ct);

        if (result.Success)
            await auditService.LogAsync("ResolveReportCase", "ReportCase", input.caseId.ToString(),
                $"Status={input.status}, Action={input.applyAction}");

        return new UserActionResult(result.Success, result.Error);
    }

    public async Task<UserActionResult> ReopenReportCase(Guid caseId, string? note, CancellationToken ct = default)
    {
        var result = await Reports.ReopenCaseAsync(caseId, CurrentOperatorId, note, ct);

        if (result.Success)
            await auditService.LogAsync("ReopenReportCase", "ReportCase", caseId.ToString(), note);

        return new UserActionResult(result.Success, result.Error);
    }

    public async Task<AdminUserTrustCard> GetUserTrustCard(Guid userId, CancellationToken ct = default)
    {
        var trustInfo = await grainFactory.GetGrain<IUserTrustGrain>(userId).GetTrustScoreAsync(ct);
        var facts     = await AdminUsers.GetTrustFactsAsync(userId, ct);

        return ToTrustCard(userId, trustInfo, facts);
    }

    public async Task<AdminUserTrustCard> RecalculateUserTrust(Guid userId, CancellationToken ct = default)
    {
        var trustInfo = await grainFactory.GetGrain<IUserTrustGrain>(userId).RecalculateTrustAsync(ct);

        await auditService.LogAsync("RecalculateUserTrust", "User", userId.ToString(),
            $"NewScore={trustInfo.trustScore}");

        var facts = await AdminUsers.GetTrustFactsAsync(userId, ct);

        return ToTrustCard(userId, trustInfo, facts);
    }

    private static AdminUserTrustCard ToTrustCard(Guid userId, UserTrustInfo trustInfo, AdminTrustFacts facts)
        => new(
            userId,
            facts.Username ?? "unknown",
            trustInfo.trustScore,
            trustInfo.totalReportsReceived,
            trustInfo.confirmedReportsReceived,
            trustInfo.totalReportsFiled,
            trustInfo.falseReportsFiled,
            facts.AutoActionsApplied,
            facts.CreatedAt is { } createdAt ? DateTimeOffset.UtcNow - createdAt : TimeSpan.Zero,
            facts.UpdatedAt?.UtcDateTime ?? DateTime.UtcNow
        );

    private static AdminReportEntry ToEntry(ReportEntryView r)
        => new(
            r.ReportId,
            r.ReporterId,
            r.ReporterUsername,
            new ReportTarget(r.TargetKind, r.TargetId, r.ChannelId, r.MessageId is { } message ? (ulong)message : null),
            r.TargetDisplayName,
            r.Category,
            r.Reason,
            r.AdditionalInfo,
            r.Status,
            null,
            r.AssignedOperatorId,
            r.ResolutionNote,
            r.CreatedAt,
            r.ResolvedAt,
            r.CaseId,
            r.PriorityScore,
            r.EscalationRule,
            r.IsIndependent);

    private static AdminReportCaseSummary ToSummary(ReportCaseSummary c)
        => new(
            c.CaseId,
            new ReportTarget(c.TargetKind, c.TargetId, c.ChannelId, c.MessageId is { } message ? (ulong)message : null),
            c.TargetDisplayName,
            c.Status,
            c.TopCategory,
            c.PriorityScore,
            c.ReportCount,
            c.IndependentReporterCount,
            c.IsEscalated,
            c.EscalationRule,
            c.AssignedOperatorId,
            c.FirstReportedAt,
            c.LastReportedAt,
            c.ResolvedAt,
            c.AppliedAction);

    #region Inactivity deletion queue

    // The console's half of the account-retention rework. The daily sweep used to call
    // RequestAutoDeleteAsync itself and erase dormant accounts on a timer; it now writes candidates into
    // IAccountDeletionQueueGrain and the decision is made here, by a person, with an audit line behind it.
    // Nothing in this section deletes anything: approving forwards to the same guarded grain call a
    // person's own deletion request goes through, so lockdown, an active subscription, an owned space and
    // a standing refusal all still bar it.

    private IAccountDeletionQueueGrain DeletionQueue
        => grainFactory.GetGrain<IAccountDeletionQueueGrain>(IAccountDeletionQueueGrain.SingletonId);

    /// <summary>
    /// One page of the queue, joined against the accounts it names.
    /// </summary>
    /// <remarks>
    /// <para>The grain stores ids and the scan's arithmetic and nothing else — a name or an address
    /// written into grain state would be a copy that goes stale and a second place personal data lives.
    /// The display fields are read here, on the way out.</para>
    ///
    /// <para><b>Read past the soft-delete filter, and no row is ever dropped.</b> Defect R21: every
    /// <c>ArgonEntity</c> carries the global <c>!IsDeleted</c> filter, and step three of an erasure sets
    /// exactly that flag — so an approved account disappeared from this page the moment its deletion
    /// started running, which is the window the entry exists for. The page rendered nine rows over a
    /// total of ten, paging drifted by the number of erasures in flight, and an operator looking for the
    /// outcome of their own approval found nothing at all. A row whose account cannot be read is now
    /// rendered with blanks rather than skipped: an entry with no user behind it is a fact an operator
    /// should see, not one to hide, and <c>TotalCount</c> stays the queue's own count because the page no
    /// longer disagrees with it.</para>
    /// </remarks>
    public async Task<AccountDeletionQueuePage> GetAccountDeletionQueue(int offset, int limit, CancellationToken ct = default)
    {
        var snapshot = await DeletionQueue.ListAsync(offset, limit);

        if (snapshot.Entries.Count == 0)
            return new AccountDeletionQueuePage(
                new IonArray<AccountDeletionQueueEntry>([]), snapshot.TotalCount, snapshot.Offset, snapshot.Limit);

        var accounts = await ReadQueuedAccountsAsync(snapshot.Entries.Select(entry => entry.UserId).ToList(), ct);

        var entries = snapshot.Entries
           .Select(entry =>
            {
                var account = accounts.GetValueOrDefault(entry.UserId);

                return new AccountDeletionQueueEntry(
                    entry.UserId,
                    account?.Username ?? "",
                    account?.DisplayName ?? "",
                    account?.Email ?? "",
                    entry.LastActivityAt,
                    entry.ThresholdMonths,
                    entry.Reason,
                    entry.EnqueuedAt,
                    StateOf(entry.State),
                    entry.DecidedByOperatorId,
                    entry.DecidedByOperatorEmail,
                    entry.DecidedAt,
                    entry.ScheduledDeletionAt,
                    entry.StrandedSince,
                    entry.CompletedAt);
            })
           .ToList();

        return new AccountDeletionQueuePage(
            new IonArray<AccountDeletionQueueEntry>(entries), snapshot.TotalCount, snapshot.Offset, snapshot.Limit);
    }

    /// <summary>How one entry's state reaches an operator's screen.</summary>
    /// <remarks>
    /// <para>Defect F9. This used to be <c>Pending ? PENDING : APPROVED</c>, and the wire enum had no
    /// third member to map to, so a <see cref="QueuedAccountDeletionState.Stranded"/> entry — an
    /// erasure that started, gave up with steps left undone, and will be picked up by nobody — was
    /// rendered as an ordinary approval. <see cref="IAccountDeletionQueueGrain.ListAsync"/> takes care
    /// to sort those into the middle band precisely because they are work owed rather than work in
    /// progress, and the mapping then flattened the distinction away: an operator scanning the queue
    /// read "Approved" against erasures that had been stuck for days, with a non-null
    /// <c>strandedSince</c> as the only sign, and never opened the stranded page.</para>
    ///
    /// <para><c>AccountDeletionQueueEntryState</c> gained <c>STRANDED</c> for this. Appending to an ion
    /// enum is the safe direction — a reader that does not declare the member decodes it verbatim
    /// through the generated open-enum helpers rather than rejecting the message — and the operator
    /// console is regenerated with the schema in any case. An exhaustive switch here rather than a
    /// second ternary, so the next state the grain-side enum grows is a compile-time question and not
    /// another silent APPROVED.</para>
    ///
    /// <para>Which is what happened next: <c>COMPLETED</c>, the state an entry reaches when the erasure
    /// it authorised has run and it is being kept for <c>AccountDeletion:DecisionRetention</c> as the
    /// record of a decision that took effect. It is the same argument as F9 one step further along the
    /// lifecycle — an "Approved" badge over a finished erasure reads as one still inside its grace
    /// period, where the account holder can yet call it off — and it is the badge that makes the
    /// retention window legible, since the row is on the page for a reason an operator can otherwise
    /// only guess at.</para>
    /// </remarks>
    private static AccountDeletionQueueEntryState StateOf(QueuedAccountDeletionState state)
        => state switch
        {
            QueuedAccountDeletionState.Pending   => AccountDeletionQueueEntryState.PENDING,
            QueuedAccountDeletionState.Approved  => AccountDeletionQueueEntryState.APPROVED,
            QueuedAccountDeletionState.Stranded  => AccountDeletionQueueEntryState.STRANDED,
            QueuedAccountDeletionState.Completed => AccountDeletionQueueEntryState.COMPLETED,
            _                                    => throw new ArgumentOutOfRangeException(
                nameof(state), state, "the deletion queue grew a state the console has no badge for")
        };

    /// <summary>
    /// The erasures that gave up half way, with the identity of the account each one left behind.
    /// </summary>
    /// <remarks>
    /// <para>Defect R5. An erasure that spends its attempts unregisters its own poll and is never armed
    /// again, and everything that could have shown that state used to erase it: the account holder cannot
    /// sign in to ask (their password digest and address are gone by step three), the scan will never
    /// propose an anonymised row again, and the queue retired the entry as soon as the deletion stopped
    /// running. This is the page that keeps it, and <see cref="ResumeAccountDeletion"/> is the button on
    /// it.</para>
    ///
    /// <para>Every account here is a tombstone by definition, so the join reads past the soft-delete
    /// filter — the same reason <see cref="GetAccountDeletionQueue"/> does — and what it renders is
    /// "Deleted Account" and a <c>deleted_…</c> username. That is the point rather than a wart: the row
    /// exists to say which account is in this state, and its id is the handle.</para>
    /// </remarks>
    public async Task<StrandedAccountDeletionPage> GetStrandedAccountDeletions(int offset, int limit, CancellationToken ct = default)
    {
        var snapshot = await DeletionQueue.ListStrandedAsync(offset, limit);

        if (snapshot.Entries.Count == 0)
            return new StrandedAccountDeletionPage(
                new IonArray<StrandedAccountDeletion>([]), snapshot.TotalCount, snapshot.Offset, snapshot.Limit);

        var accounts = await ReadQueuedAccountsAsync(snapshot.Entries.Select(entry => entry.UserId).ToList(), ct);

        var entries = snapshot.Entries
           .Select(entry =>
            {
                var account = accounts.GetValueOrDefault(entry.UserId);

                return new StrandedAccountDeletion(
                    entry.UserId,
                    account?.Username ?? "",
                    account?.DisplayName ?? "",
                    account?.Email ?? "",
                    entry.DecidedByOperatorId,
                    entry.DecidedByOperatorEmail,
                    entry.DecidedAt,
                    entry.ScheduledDeletionAt,
                    entry.StrandedSince,
                    entry.FailureReason,
                    entry.ExecutionAttempts);
            })
           .ToList();

        return new StrandedAccountDeletionPage(
            new IonArray<StrandedAccountDeletion>(entries), snapshot.TotalCount, snapshot.Offset, snapshot.Limit);
    }

    /// <summary>The display fields of the accounts a queue page names, erased ones included.</summary>
    private Task<Dictionary<Guid, AdminAccountIdentity>> ReadQueuedAccountsAsync(List<Guid> ids, CancellationToken ct)
        => AdminUsers.GetAccountIdentitiesAsync(ids, ct);

    /// <summary>
    /// Approves one queued account: the deletion is scheduled and the account is told by e-mail.
    /// </summary>
    /// <remarks>
    /// The refusal the deletion grain can still give is passed back verbatim rather than flattened into
    /// "failed": between the sweep that proposed the account and this click it may have been put under
    /// lockdown, bought a subscription or been left owning a space, and which of those it is decides what
    /// an operator does next.
    /// </remarks>
    public async Task<UserActionResult> ApproveAccountDeletion(Guid userId, CancellationToken ct = default)
    {
        // Written before the irreversible act, so the intent survives whatever happens next — defect R7.
        // The audit log is where an operator review looks, and it used to be written only after the queue
        // had already scheduled the erasure and mailed the account, only on the success path, and inside
        // the try whose catch reports a failure. An audit store that was briefly unavailable therefore
        // erased the one record of who ordered the deletion, and a refusal — an operator repeatedly
        // trying to approve a locked or space-owning account — was never recorded at all.
        await AuditQuietlyAsync("ApproveAccountDeletion", userId, "Outcome=attempted");

        try
        {
            var caller   = OperatorRequestContext.Current;
            var decision = await DeletionQueue.ApproveAsync(userId, caller.OperatorId, caller.Email);

            if (!decision.Success)
            {
                await AuditQuietlyAsync("ApproveAccountDeletion", userId,
                    $"Outcome=refused; Error={decision.Error}; RequestError={decision.RequestError}");

                return new UserActionResult(false, decision.Error switch
                {
                    AccountDeletionQueueDecisionError.NotQueued =>
                        "That account is not in the deletion queue",
                    AccountDeletionQueueDecisionError.AlreadyDecided =>
                        "That account has already been approved",
                    AccountDeletionQueueDecisionError.RefusedByDeletionGrain =>
                        $"Deletion refused: {decision.RequestError}",
                    _ => "Could not approve the deletion"
                });
            }

            // Outside the result-bearing path on purpose — defect R27. By here the deletion is scheduled,
            // the entry is Approved and the account has its notice mail; an audit write that throws must
            // not turn that into "failed" on the operator's screen, because their next click answers
            // "already approved" and they are left with two contradictory truths about an erasure that is
            // going to happen either way.
            await AuditQuietlyAsync("ApproveAccountDeletion", userId,
                $"Outcome=scheduled; ScheduledFor={decision.ScheduledDeletionAt:O}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to approve the queued deletion of {UserId}", userId);

            await AuditQuietlyAsync("ApproveAccountDeletion", userId, $"Outcome=error; {ex.Message}");

            return new UserActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Writes an audit line and never lets its failure become the caller's.
    /// </summary>
    /// <remarks>
    /// The audit store being unavailable is exactly the moment you least want to lose a record, and also
    /// the moment when reporting its failure is most misleading: the operator's action either happened or
    /// it did not, and that is what the result has to say. A write that cannot land is logged at Critical
    /// — the application log is the last copy of it.
    /// </remarks>
    private async Task AuditQuietlyAsync(string action, Guid userId, string details)
    {
        try
        {
            await auditService.LogAsync(action, "User", userId.ToString(), details);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Could not write the operator audit line for {Action} on {UserId} ({Details}); "
              + "this log line is the only remaining record of it", action, userId, details);
        }
    }

    /// <summary>
    /// Rejects one queued account: the entry goes, and the sweep leaves that account alone for a full
    /// inactivity period.
    /// </summary>
    /// <remarks>
    /// The hold is the point. An entry is a projection of the last sweep, so a rejection that only removed
    /// it would last until the next pass and the same account would be proposed again tomorrow — which is
    /// defect CON-4 (a refusal the system cannot remember) with an operator in the account holder's place.
    /// </remarks>
    public async Task<UserActionResult> RejectAccountDeletion(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var caller   = OperatorRequestContext.Current;
            var decision = await DeletionQueue.RejectAsync(userId, caller.OperatorId, caller.Email);

            if (!decision.Success)
            {
                await AuditQuietlyAsync("RejectAccountDeletion", userId,
                    $"Outcome=refused; Error={decision.Error}");

                return new UserActionResult(false, decision.Error switch
                {
                    AccountDeletionQueueDecisionError.NotQueued =>
                        "That account is not in the deletion queue",
                    AccountDeletionQueueDecisionError.AlreadyDecided =>
                        "That deletion is already scheduled; it can only be cancelled by the account holder",
                    _ => "Could not reject the deletion"
                });
            }

            await AuditQuietlyAsync("RejectAccountDeletion", userId, "Outcome=declined");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reject the queued deletion of {UserId}", userId);

            await AuditQuietlyAsync("RejectAccountDeletion", userId, $"Outcome=error; {ex.Message}");

            return new UserActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Picks a stranded erasure back up: the deletion resumes from the step it reached.
    /// </summary>
    /// <remarks>
    /// <para>The operator half of defect R5. The work itself is deliberately not awaited here — an
    /// erasure is minutes of database work and one grain call per space and per file, so
    /// <see cref="IAccountDeletionGrain.ResumeAsync"/> clears the attempt count, arms the poll to fire at
    /// once and answers with the status to render. Nothing is repeated: the deletion resumes from its own
    /// record of the steps it has already done, which matters because a released file reference cannot be
    /// released a second time.</para>
    ///
    /// <para>Audited attempt-and-outcome like an approval, and for the same reason: it finishes an
    /// irreversible act on somebody's account. It is not restricted to accounts the queue knows about —
    /// a person's own deletion request can strand in exactly the same way and has no queue entry at all,
    /// and refusing to let an operator reach that account would leave the only case nobody can see.</para>
    ///
    /// <para>The queue is told afterwards so the stranded page settles immediately instead of at the next
    /// daily pass; a queue that will not answer costs a stale row for a day and nothing else, so it never
    /// turns a resumed deletion into a reported failure.</para>
    /// </remarks>
    public async Task<UserActionResult> ResumeAccountDeletion(Guid userId, CancellationToken ct = default)
    {
        await AuditQuietlyAsync("ResumeAccountDeletion", userId, "Outcome=attempted");

        try
        {
            var status = await grainFactory.GetGrain<IAccountDeletionGrain>(userId).ResumeAsync();

            if (status.Status is AccountDeletionStatusKind.None or AccountDeletionStatusKind.Completed)
            {
                await AuditQuietlyAsync("ResumeAccountDeletion", userId, $"Outcome=nothingToResume; Status={status.Status}");

                return new UserActionResult(false, status.Status is AccountDeletionStatusKind.Completed
                    ? "That account's deletion has already finished"
                    : "That account has no deletion to resume");
            }

            await AuditQuietlyAsync("ResumeAccountDeletion", userId,
                $"Outcome=resumed; Status={status.Status}; Attempts={status.ExecutionAttempts}; "
              + $"LastError={status.FailureReason}");

            try
            {
                await DeletionQueue.RefreshAsync(userId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Resumed the deletion of {UserId} but could not refresh its queue entry; the next scan will", userId);
            }

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resume the stranded deletion of {UserId}", userId);

            await AuditQuietlyAsync("ResumeAccountDeletion", userId, $"Outcome=error; {ex.Message}");

            return new UserActionResult(false, ex.Message);
        }
    }


    /// <summary>The inactivity sweep as an operator sees it: when it last ran, what it did, when it runs next.</summary>
    /// <remarks>
    /// Read-only and unaudited, unlike everything else on this page — it names no account and changes
    /// nothing. It exists because an empty queue reads the same whether nothing was proposable or
    /// nothing ran, and telling those apart used to mean reading a pod's log.
    /// </remarks>
    public async Task<AutoDeleteScanStatus> GetAutoDeleteScanStatus(CancellationToken ct = default)
        => Map(await Scheduler.GetScanStatusAsync());

    /// <summary>Runs a pass now and answers with what it did.</summary>
    /// <remarks>
    /// Audited, though a pass only ever writes to the queue: what it costs is a full read of the user
    /// table, and an operator who can trigger that on demand is worth a row in the log. It runs whatever
    /// the switch says — the switch governs the timer, not the button — and it deliberately does not
    /// move the timer, so forcing one does not postpone tomorrow's.
    /// </remarks>
    public async Task<AutoDeleteScanStatus> RunAutoDeleteScan(CancellationToken ct = default)
    {
        await AuditQuietlyAsync("RunAutoDeleteScan", Guid.Empty, "Outcome=attempted");

        try
        {
            await Scheduler.RunScanAsync();

            var status = await Scheduler.GetScanStatusAsync();

            await AuditQuietlyAsync("RunAutoDeleteScan", Guid.Empty,
                $"Outcome=ran; Processed={status.LastProcessed}; Proposed={status.LastProposed}; "
              + $"Enqueued={status.LastEnqueued}; Retired={status.LastRetired}; Length={status.LastQueueLength}");

            return Map(status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An operator's forced inactivity scan failed");
            await AuditQuietlyAsync("RunAutoDeleteScan", Guid.Empty, $"Outcome=error; {ex.Message}");

            return Map(await Scheduler.GetScanStatusAsync());
        }
    }

    /// <summary>What the platform mailed, for a fortnight.</summary>
    /// <remarks>
    /// <para>The questions this answers — did the reset code go out, why did this person get a deletion
    /// notice — had no answer at all before: a send is a log line on whichever pod made it, and the log
    /// line is gone by the time anybody asks.</para>
    ///
    /// <para>It holds no address, no subject and no body. An account id, which template it was, when,
    /// and whether it left the building. That is enough to answer the question and not enough to be a
    /// record of who was mailed where.</para>
    /// </remarks>
    public Task<EmailJournalPage> GetEmailJournal(Guid? userId, int offset, int limit, CancellationToken ct = default)
        => AdminPlatform.ReadEmailJournalAsync(userId, offset, limit, ct);

    /// <summary>The erasures under way right now, whoever started them.</summary>
    /// <remarks>
    /// <para>A deletion lives in one grain per account and nothing indexes those, so this reads the
    /// register the deletion grain writes as it arms, executes, finishes or is called off. Both kinds
    /// end up in it: an operator's approval and a person deleting their own account from the console,
    /// which never touches the queue at all and was previously invisible here.</para>
    ///
    /// <para>The names are read with <c>IgnoreQueryFilters</c> and may be blank. That is not a defect
    /// to paper over: the erasure anonymises the row on its third step of eleven, so an account halfway
    /// through genuinely no longer has a name or an address, and showing the id alone is the honest
    /// answer. The register's own copy of the status is trusted for the list — asking every grain would
    /// turn a page into a fan-out — and the impact panel re-reads the one account an operator opens.</para>
    /// </remarks>
    public async Task<InFlightDeletionPage> GetDeletionsInFlight(int offset, int limit, CancellationToken ct = default)
    {
        var snapshot = await DeletionQueue.ListInFlightAsync(offset, limit);

        if (snapshot.Entries.Count == 0)
            return new InFlightDeletionPage(new IonArray<InFlightDeletionEntry>([]),
                snapshot.TotalCount, snapshot.FailedCount, offset, limit);

        var accounts = await ReadQueuedAccountsAsync(snapshot.Entries.Select(entry => entry.UserId).ToList(), ct);

        var rows = new List<InFlightDeletionEntry>(snapshot.Entries.Count);

        foreach (var entry in snapshot.Entries)
        {
            accounts.TryGetValue(entry.UserId, out var account);

            // One grain call per row on the page, not per account in the register: the failure and the
            // attempt count are the two things an operator acts on and the register does not carry them.
            var status = await grainFactory.GetGrain<IAccountDeletionGrain>(entry.UserId).GetDeletionStatusAsync();

            // And the row that the register still believes in but the account does not. The register is
            // written one-way, so an update can be lost; the account's own grain is the fact, and a
            // deletion that is over does not belong on a list of work in progress.
            if (status.Status is AccountDeletionStatusKind.None or AccountDeletionStatusKind.Completed)
                continue;

            rows.Add(new InFlightDeletionEntry(
                entry.UserId,
                account?.Username ?? "",
                account?.DisplayName ?? "",
                account?.Email ?? "",
                status.Status switch
                {
                    AccountDeletionStatusKind.Scheduled => AccountDeletionStatusView.SCHEDULED,
                    AccountDeletionStatusKind.Executing => AccountDeletionStatusView.EXECUTING,
                    AccountDeletionStatusKind.Completed => AccountDeletionStatusView.COMPLETED,
                    AccountDeletionStatusKind.Failed    => AccountDeletionStatusView.FAILED,
                    _                                   => AccountDeletionStatusView.NONE
                },
                entry.ArmedAt.UtcDateTime,
                (status.ExecutionAt ?? entry.ExecutionAt)?.UtcDateTime,
                entry.SelfRequested,
                status.FailureReason,
                status.ExecutionAttempts));
        }

        return new InFlightDeletionPage(new IonArray<InFlightDeletionEntry>(rows),
            snapshot.TotalCount, snapshot.FailedCount, offset, limit);
    }

    /// <summary>What erasing one account would actually destroy.</summary>
    /// <remarks>
    /// Computed by <see cref="IAdminUsersGrain.GetAccountDeletionImpactAsync"/>, on the role where the
    /// sweep's threshold and the deletion grain both live — see there for what each figure means.
    /// </remarks>
    public Task<AccountDeletionImpact> GetAccountDeletionImpact(Guid userId, CancellationToken ct = default)
        => AdminUsers.GetAccountDeletionImpactAsync(userId, ct);

    /// <summary>Starts the deletion workflow on an account nobody proposed.</summary>
    /// <remarks>
    /// The countdown an approval arms, reached without a queue entry: the ordinary grace period, the
    /// notice mail now, and an account that can still call it off from its own console or by signing in.
    /// Every bar stands. Not restricted to dormant accounts on purpose — the sweep decides what is
    /// dormant, an operator decides what needs deleting, and support tickets are the second kind.
    /// </remarks>
    public Task<UserActionResult> StartAccountDeletion(Guid userId, CancellationToken ct = default)
        => DriveDeletionAsync("StartAccountDeletion", userId, grain => grain.StartByOperatorAsync());

    /// <summary>Brings an armed erasure forward to now.</summary>
    /// <remarks>
    /// Refused unless something is already counting down: this button shortens a decision, it does not
    /// take one. The erasure runs on the poll that follows, which is armed to fire immediately.
    /// </remarks>
    public Task<UserActionResult> ExpireAccountDeletionGrace(Guid userId, CancellationToken ct = default)
        => DriveDeletionAsync("ExpireAccountDeletionGrace", userId, grain => grain.ExpireGraceAsync());

    /// <summary>Erases an account immediately, with no mail to it at all.</summary>
    /// <remarks>
    /// The last-resort button: no grace, no notice, no confirmation, and the erasure runs inside this
    /// call rather than on the next poll, so the answer says whether it actually finished. The bars still
    /// stand — they are the platform's invariants, not a courtesy to the account holder.
    /// </remarks>
    public Task<UserActionResult> EraseAccountNow(Guid userId, CancellationToken ct = default)
        => DriveDeletionAsync("EraseAccountNow", userId, grain => grain.EraseNowAsync());

    /// <summary>The three operator-driven deletion buttons, which differ only in which call they make.</summary>
    /// <remarks>
    /// Audited attempt-then-outcome like the queue's own decisions, and for the same reason: each one is
    /// a step towards an irreversible act on somebody's account, and the row that says who asked has to
    /// survive whether or not the call succeeded.
    /// </remarks>
    private async Task<UserActionResult> DriveDeletionAsync(
        string action, Guid userId, Func<IAccountDeletionGrain, ValueTask<AccountDeletionRequestResult>> drive)
    {
        await AuditQuietlyAsync(action, userId, "Outcome=attempted");

        try
        {
            var result = await drive(grainFactory.GetGrain<IAccountDeletionGrain>(userId));

            if (!result.Success)
            {
                await AuditQuietlyAsync(action, userId, $"Outcome=refused; Reason={result.Error}");
                return new UserActionResult(false, Explain(result.Error));
            }

            await AuditQuietlyAsync(action, userId, $"Outcome=done; ExecutionAt={result.ScheduledDeletionAt:O}");

            // So the queue's own page settles now rather than at the next pass; a queue that will not
            // answer costs a stale row and nothing else.
            try
            {
                await DeletionQueue.RefreshAsync(userId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Drove the deletion of {UserId} but could not refresh its queue entry; the next scan will", userId);
            }

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Operator action {Action} failed for {UserId}", action, userId);
            await AuditQuietlyAsync(action, userId, $"Outcome=error; {ex.Message}");
            return new UserActionResult(false, ex.Message);
        }
    }

    /// <summary>The grain's refusal, in words a console can show.</summary>
    private static string Explain(AccountDeletionRequestError? error)
        => error switch
        {
            AccountDeletionRequestError.AlreadyScheduled      => "That account's deletion is already under way",
            AccountDeletionRequestError.NotScheduled          => "That account has no deletion counting down",
            AccountDeletionRequestError.HasActiveSubscription => "That account has an active subscription",
            AccountDeletionRequestError.OwnsSpaces            => "That account still owns a space",
            AccountDeletionRequestError.AccountLocked         => "That account is under a standing lockdown",
            AccountDeletionRequestError.ServiceAccount        => "That is a bot or platform account and cannot be deleted",
            AccountDeletionRequestError.RecentlyDeclined      => "That account recently refused a deletion",
            AccountDeletionRequestError.InvalidPassword       => "The account's password was not accepted",
            _                                                 => "The server refused the request"
        };

    private IAutoDeleteSchedulerGrain Scheduler
        => grainFactory.GetGrain<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId);

    private static AutoDeleteScanStatus Map(AutoDeleteScanReport report)
        => new(report.Enabled,
            report.ArmedAt?.UtcDateTime,
            report.NextDueAt?.UtcDateTime,
            report.LastStartedAt?.UtcDateTime,
            report.LastFinishedAt?.UtcDateTime,
            report.LastTrigger,
            report.Runs,
            report.LastProcessed,
            report.LastProposed,
            report.LastEnqueued,
            report.LastRetired,
            report.LastHeld,
            report.LastQueueLength,
            report.LastError,
            report.LastErrorAt?.UtcDateTime,
            report.DefaultThresholdMonths);

    #endregion

    #region Feature Flags

    public async Task<FeatureFlagList> GetFeatureFlags(CancellationToken ct = default)
    {
        var grain = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var flags = await grain.ListFlagsAsync();

        var summaries = flags.Select(f => new FeatureFlagSummary(
            f.Id,
            f.Description,
            f.DefaultEnabled,
            f.RolloutPercentage,
            f.HasVariants,
            f.UssdActivationCode,
            f.ExpiresAt?.UtcDateTime,
            f.OverrideCount,
            f.CreatedAt.UtcDateTime)).ToList();

        return new FeatureFlagList(new IonArray<FeatureFlagSummary>(summaries));
    }

    public async Task<FeatureFlagDetails> GetFeatureFlag(string flagId, CancellationToken ct = default)
    {
        var grain = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var flag  = await grain.GetFlagAsync(flagId);
        if (flag is null)
            throw new InvalidOperationException($"Feature flag '{flagId}' not found");

        var overrides = flag.Overrides.Select(o => new FeatureFlagOverrideInfo(
            o.OverrideId,
            (int)o.Scope,
            o.TargetId,
            o.Enabled,
            o.RolloutPercentage,
            o.ForcedVariant,
            o.CreatedAt.UtcDateTime)).ToList();

        return new FeatureFlagDetails(
            flag.Id,
            flag.Description,
            flag.DefaultEnabled,
            flag.RolloutPercentage,
            flag.Variants,
            flag.UssdActivationCode,
            flag.ExpiresAt?.UtcDateTime,
            flag.CreatedAt.UtcDateTime,
            new IonArray<FeatureFlagOverrideInfo>(overrides));
    }

    public async Task<FeatureFlagActionResult> CreateFeatureFlag(CreateFeatureFlagInput input, CancellationToken ct = default)
    {
        var grain  = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var result = await grain.CreateFlagAsync(new FeatureFlagInput(
            input.flagId, input.description, input.defaultEnabled, input.rolloutPercentage,
            input.variants, input.ussdActivationCode, ToOffset(input.expiresAt)));

        if (result.Success)
            await auditService.LogAsync("CreateFeatureFlag", "FeatureFlag", input.flagId,
                $"Created feature flag '{input.flagId}', ussd={input.ussdActivationCode ?? "-"}");

        return new FeatureFlagActionResult(result.Success, result.FlagId, result.Error);
    }

    public async Task<FeatureFlagActionResult> UpdateFeatureFlag(UpdateFeatureFlagInput input, CancellationToken ct = default)
    {
        var grain  = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var result = await grain.UpdateFlagAsync(new FeatureFlagInput(
            input.flagId, input.description, input.defaultEnabled, input.rolloutPercentage,
            input.variants, input.ussdActivationCode, ToOffset(input.expiresAt)));

        if (result.Success)
            await auditService.LogAsync("UpdateFeatureFlag", "FeatureFlag", input.flagId,
                $"Updated feature flag '{input.flagId}'");

        return new FeatureFlagActionResult(result.Success, result.FlagId, result.Error);
    }

    public async Task<FeatureFlagActionResult> DeleteFeatureFlag(string flagId, CancellationToken ct = default)
    {
        var grain  = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var result = await grain.DeleteFlagAsync(flagId);

        if (result.Success)
            await auditService.LogAsync("DeleteFeatureFlag", "FeatureFlag", flagId, $"Deleted feature flag '{flagId}'");

        return new FeatureFlagActionResult(result.Success, result.FlagId, result.Error);
    }

    public async Task<FeatureFlagActionResult> SetFeatureFlagOverride(SetFeatureFlagOverrideInput input, CancellationToken ct = default)
    {
        var grain = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var scope = (FeatureFlagScope)input.scope;

        // User-scope enable routes through the activation path so the user gets the same realtime event.
        if (scope == FeatureFlagScope.User && input.enabled == true)
        {
            if (!Guid.TryParse(input.targetId, out var userId))
                return new FeatureFlagActionResult(false, input.flagId, "Target id must be a user GUID for User scope");

            var activation = await grain.ActivateForUserAsync(userId, input.flagId);
            if (!activation.IsEnabled)
                return new FeatureFlagActionResult(false, input.flagId, "Activation failed (flag missing or expired)");

            await NotifyUserAsync(userId, new FeatureFlagActivated(userId, input.flagId, true, activation.Variant));
            await auditService.LogAsync("SetFeatureFlagOverride", "FeatureFlag", input.flagId,
                $"Activated flag '{input.flagId}' for user {userId} (User scope)");

            return new FeatureFlagActionResult(true, input.flagId, null);
        }

        var result = await grain.SetOverrideAsync(new FeatureFlagOverrideInput(
            input.flagId, scope, input.targetId, input.enabled, input.rolloutPercentage, input.forcedVariant));

        if (result.Success)
            await auditService.LogAsync("SetFeatureFlagOverride", "FeatureFlag", input.flagId,
                $"Set override scope={scope} target={input.targetId} enabled={input.enabled}");

        return new FeatureFlagActionResult(result.Success, result.FlagId, result.Error);
    }

    public async Task<FeatureFlagActionResult> DeleteFeatureFlagOverride(Guid overrideId, CancellationToken ct = default)
    {
        var grain  = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var result = await grain.DeleteOverrideAsync(overrideId);

        if (result.Success)
            await auditService.LogAsync("DeleteFeatureFlagOverride", "FeatureFlag", result.FlagId,
                $"Deleted override {overrideId}");

        return new FeatureFlagActionResult(result.Success, result.FlagId, result.Error);
    }

    // ── Tenant directory (self-hosted / enterprise routing) ──────────────────────────────────

    public Task<TenantDirectoryList> GetTenantDirectory(CancellationToken ct = default)
        => AdminPlatform.GetTenantDirectoryAsync(ct);

    public async Task<TenantActionResult> CreateTenant(CreateTenantInput input, CancellationToken ct = default)
    {
        try
        {
            var domain = NormalizeTenantDomain(input.domain);
            if (domain is null)
                return new TenantActionResult(false, null, "A valid email domain is required");
            if (!IsValidInstanceUrl(input.instanceUrl))
                return new TenantActionResult(false, null, "Instance URL must be an absolute https URL");

            var instanceUrl = input.instanceUrl.Trim();
            var result = await AdminPlatform.CreateTenantAsync(domain, instanceUrl, input.orgName?.Trim(), input.ownerUserId,
                input.notes?.Trim(), ct);

            if (result.success)
                await auditService.LogAsync("CreateTenant", "Tenant", result.tenantId.ToString(),
                    $"Created tenant domain='{domain}' url='{instanceUrl}' (unverified)");

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create tenant");
            return new TenantActionResult(false, null, "Failed to create tenant");
        }
    }

    public async Task<TenantActionResult> UpdateTenant(UpdateTenantInput input, CancellationToken ct = default)
    {
        try
        {
            if (!IsValidInstanceUrl(input.instanceUrl))
                return new TenantActionResult(false, input.tenantId, "Instance URL must be an absolute https URL");

            var change = await AdminPlatform.UpdateTenantAsync(input.tenantId, input.instanceUrl.Trim(), input.orgName?.Trim(),
                input.notes?.Trim(), ct);

            if (change.Result.success)
                await auditService.LogAsync("UpdateTenant", "Tenant", input.tenantId.ToString(), $"Updated tenant '{change.Domain}'");

            return change.Result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update tenant={TenantId}", input.tenantId);
            return new TenantActionResult(false, input.tenantId, "Failed to update tenant");
        }
    }

    public async Task<TenantActionResult> SetTenantVerified(Guid tenantId, bool isVerified, CancellationToken ct = default)
    {
        try
        {
            var change = await AdminPlatform.SetTenantVerifiedAsync(CurrentOperatorId, tenantId, isVerified, ct);

            if (change.Result.success)
                await auditService.LogAsync("SetTenantVerified", "Tenant", tenantId.ToString(),
                    $"Set verified={isVerified} for '{change.Domain}'");

            return change.Result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set tenant verified={TenantId}", tenantId);
            return new TenantActionResult(false, tenantId, "Failed to set tenant verification");
        }
    }

    public async Task<TenantActionResult> DeleteTenant(Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            var change = await AdminPlatform.DeleteTenantAsync(tenantId, ct);

            if (change.Result.success)
                await auditService.LogAsync("DeleteTenant", "Tenant", tenantId.ToString(), $"Deleted tenant '{change.Domain}'");

            return change.Result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete tenant={TenantId}", tenantId);
            return new TenantActionResult(false, tenantId, "Failed to delete tenant");
        }
    }

    private static string? NormalizeTenantDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        var d  = domain.Trim().ToLowerInvariant();
        var at = d.LastIndexOf('@');
        if (at >= 0) d = d[(at + 1)..]; // tolerate a full email being pasted in
        if (d.Length == 0 || !d.Contains('.') || d.Any(char.IsWhiteSpace)) return null;
        return d;
    }

    private static bool IsValidInstanceUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        // allow plain http only for localhost dev instances
        return uri.Scheme == Uri.UriSchemeHttp && uri.Host is "localhost" or "127.0.0.1";
    }

    /// <summary>
    /// Kept as an identity now that the contract speaks <c>DateTimeOffset</c> directly.
    /// </summary>
    /// <remarks>
    /// The call sites read as "convert on the way in", which is still the right shape for them to
    /// have — the conversion simply has nothing left to do since ion's <c>datetime</c> stopped being
    /// a <c>DateTime</c>.
    /// </remarks>
    private static DateTimeOffset? ToOffset(DateTimeOffset? dt) => dt;

    private async Task NotifyUserAsync<T>(Guid userId, T payload) where T : IArgonEvent
    {
        var sessions = await sessionDiscovery.GetUserSessionsAsync(userId);
        if (sessions.Count == 0)
            return;

        await sessionNotifier.NotifySessionsAsync(sessions, payload);
    }

    #endregion
}
