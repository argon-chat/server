namespace Argon.Api.Clustering;

using Argon.Api.Grains.Interfaces;
using Argon.Core.Grains.Interfaces;
using Argon.Features.Clustering;
using Argon.Grains;
using Argon.Grains.Interfaces;
using Grains;

public sealed class CoreRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Core;

    public string Description   => "space, channel, identity, session, bot runtime, dev teams";
    public bool   IsClient      => false;
    public bool   UsesReminders => true;

    /// <summary>
    /// The role clients connect through, and the only one.
    /// </summary>
    /// <remarks>
    /// <para>A gateway is where client connections land and where a message is forwarded to whichever
    /// silo holds the activation, so being one adds latency-sensitive forwarding work to a role. The
    /// rule that follows is: a silo should be a gateway exactly when losing it already means the
    /// calls it would forward are unavailable. Anything else adds a way to lose client connections
    /// while buying nothing — a gateway on <c>jobs</c> keeps clients connected to a cluster whose
    /// channels they cannot call.</para>
    ///
    /// <para>Core is where the calls go. Of the grains reached from outside, <c>IChannelGrain</c>,
    /// <c>ISpaceGrain</c>, <c>IUserGrain</c> and <c>ISecurityGrain</c> account for most of the call
    /// sites and all live here, so most client traffic is forwarded within this role. It is also the
    /// role that runs the most replicas, which is where gateway redundancy comes from without any
    /// extra machinery.</para>
    ///
    /// <para><b>A draining silo does not leave the gateway list.</b> Gateway list providers filter on
    /// <c>Status == Active &amp;&amp; ProxyPort != 0</c>, and draining never touches membership —
    /// <c>SiloDrainService</c> says so in as many words ("Don't manipulate Orleans membership table
    /// directly"), so a draining silo stays <c>Active</c> and keeps being handed out. Readiness going
    /// false removes the pod from the Kubernetes Service, which is what stops <em>new HTTP</em>
    /// traffic; it does nothing to Orleans clients, which dial pod addresses read from the membership
    /// table. What actually retires a gateway is the process stopping.</para>
    ///
    /// <para>The cost is one extra hop for a call to a grain that lives elsewhere: entry point to a
    /// core gateway to the target silo. Those calls — commerce, media, jobs — are already doing
    /// database or object-storage work that dwarfs a hop on the same network.</para>
    /// </remarks>
    public bool ExposesClusterGateway => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<RegionRegistryFeature>();
        features.Add<SiloLifecycleFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<MessagePipeFeature>();
        features.Add<RepositoriesFeature>();
        features.Add<PermissionsFeature>();
        features.Add<ArchetypeCacheFeature>();
        features.Add<MessagesFeature>();
        features.Add<PresenceFeature>();
        features.Add<NotificationsFeature>();
        features.Add<OtpFeature>();
        features.Add<SocialFeature>();
        features.Add<GeoIpFeature>();
        features.Add<SfuFeature>();
        features.Add<KlipyFeature>();
        features.Add<LinkPreviewFeature>();
        features.Add<RealtimeBusFeature>();
        features.Add<ArgonAuthorizationFeature>();
        features.Add<FileStorageFeature>();
        features.Add<OperatorAuthFeature>();
        features.Add<CosmeticsFeature>();
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
    {
        registry.AddToRef<SpaceGrain>();
        registry.AddToRef<SpaceReadGrain>();

        registry.AddToRef<ChannelGrain>();
        registry.AddToRef<VoiceBroadcastGrain>();
        registry.AddToRef<UserGrain>();
        registry.AddToRef<UserSessionGrain>();
        registry.AddToRef<CosmeticsGrain>();
        registry.AddToRef<CosmeticsReadGrain>();
        registry.AddToRef<UserPresenceGrain>();
        registry.AddToRef<BotGatewayGrain>();
        registry.AddToRef<ServerInviteGrain>();
        registry.AddToRef<InviteGrain>();
        registry.AddToRef<SpaceDeletionGrain>();
        registry.AddToRef<UserStatsGrain>();
        registry.AddToRef<UserChatGrain>();
        registry.AddToRef<FriendsGrain>();
        registry.AddToRef<SavedGifsGrain>();
        registry.AddToRef<NotificationGrain>();
        registry.AddToRef<PrivacyPolicyGrain>();
        registry.AddToRef<AuthorizationGrain>();
        registry.AddToRef<SecurityGrain>();
        registry.AddToRef<FeatureFlagGrain>();
        registry.AddToRef<EntitlementGrain>();
        registry.AddToRef<OperatorAuthChallengeGrain>();
        registry.AddToRef<AdminOperatorsGrain>();
        registry.AddToRef<AppsManagementGrain>();
        registry.AddToRef<IdentityDirectoryGrain>();
        registry.AddToRef<DeviceIdentityGrain>();
        registry.AddToRef<FileDirectoryGrain>();
        registry.AddToRef<DevTeamsGrain>();
        registry.AddToRef<BotCommandsGrain>();
        registry.AddToRef<BotDirectoryGrain>();

        registry.AcceptRemote<IContentModerationGrain>("hosting it here makes core resident the ONNX models");
        registry.AcceptRemote<IFileStorageGrain>("hosting it here drags ImageSharp and the S3 client into core");
        registry.AcceptRemote<IVoiceControlGrain>("join path only; keeps the SFU wiring out of core");
        registry.AcceptRemote<IEmailManager>("keeps the SMTP and template stack in jobs");
    }
}

public sealed class VoiceRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Voice;

    public string Description => "voice control, calls, USSD";
    public bool   IsClient    => false;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<RegionRegistryFeature>();
        features.Add<SiloLifecycleFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<RepositoriesFeature>();
        features.Add<PermissionsFeature>();
        features.Add<SfuFeature>();
        features.Add<PresenceFeature>();
        features.Add<MessagesFeature>();
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
    {
        registry.AddToRef<VoiceControlGrain>();
        registry.AddToRef<CallGrain>();
        registry.AddToRef<UssdGrain>();

        registry.AcceptRemote<IFeatureFlagGrain>("single call site in UssdGrain; not worth co-hosting core's flags");
    }
}

public sealed class MediaRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Media;

    public string Description => "file storage and blob GC";
    public bool   IsClient    => false;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<RegionRegistryFeature>();
        features.Add<SiloLifecycleFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<RepositoriesFeature>();
        features.Add<FileStorageFeature>();
        features.Add<FileGcFeature>();
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
        => registry.AddToRef<FileStorageGrain>();
}

public sealed class ModerationRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Moderation;

    public string Description => "ONNX image moderation — memory-bound, low request volume";
    public bool   IsClient    => false;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<RegionRegistryFeature>();
        features.Add<SiloLifecycleFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<ContentModerationFeature>();
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
        => registry.AddToRef<ContentModerationGrain>();
}

public sealed class CommerceRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Commerce;

    public string Description   => "entitlements, boosts, inventory, levels";
    public bool   IsClient      => false;
    public bool   UsesReminders => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<RegionRegistryFeature>();
        features.Add<SiloLifecycleFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<RepositoriesFeature>();
        features.Add<PermissionsFeature>();
        features.Add<XsollaFeature>();
        features.Add<RealtimeBusFeature>();
        features.Add<PresenceFeature>();
        features.Add<NotificationsFeature>();
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
    {
        registry.AddToRef<UltimaGrain>();
        registry.AddToRef<SpaceBoostGrain>();
        registry.AddToRef<InventoryGrain>();
        registry.AddToRef<UserLevelGrain>();

        registry.AcceptRemote<IUserGrain>("single call site in UltimaGrain");
    }
}

public sealed class JobsRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Jobs;

    public string Description   => "account deletion, exports, e-mail, reports, expired-row sweep";
    public bool   IsClient      => false;
    public bool   UsesReminders => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<RegionRegistryFeature>();
        features.Add<SiloLifecycleFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<RepositoriesFeature>();
        features.Add<TemplateEngineFeature>();
        features.Add<EmailJournalFeature>();
        features.Add<AccountDeletionFeature>();
        features.Add<ReportSystemFeature>();
        features.Add<NotificationsFeature>();
        features.Add<ArgonAuthorizationFeature>();
        features.Add<PresenceFeature>();
        features.Add<FileStorageFeature>();
        features.Add<OtpFeature>();
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
    {
        registry.AddToRef<AccountDeletionGrain>();
        registry.AddToRef<AutoDeleteSchedulerGrain>();
        registry.AddToRef<AccountDeletionQueueGrain>();
        registry.AddToRef<TtlSweepGrain>();
        registry.AddToRef<ExportPumpGrain>();
        registry.AddToRef<UserDataExportGrain>();
        registry.AddToRef<EmailManager>();
        registry.AddToRef<ReportGrain>();
        registry.AddToRef<UserTrustGrain>();
        registry.AddToRef<AdminUsersGrain>();
        registry.AddToRef<AdminDirectoryGrain>();
        registry.AddToRef<AdminPlatformGrain>();

        registry.AddStartupCall<IAutoDeleteSchedulerGrain>();
        registry.AddStartupCall<ITtlSweepGrain>();

        registry.AcceptRemote<IFileStorageGrain>("cleanup path only; media owns the storage stack");
        registry.AcceptRemote<IUserGrain>(
            "reached through the authorization stack the deletion path needs; co-hosting it would " +
            "put a second copy of core's busiest worker on a role that runs batch work");
    }
}
