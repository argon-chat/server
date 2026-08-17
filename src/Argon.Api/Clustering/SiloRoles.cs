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

    public string Description   => "space, channel, identity, session, bot runtime";
    public bool   IsClient      => false;
    public bool   UsesReminders => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
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
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
    {
        registry.AddToRef<SpaceGrain>();
        registry.AddToRef<ChannelGrain>();
        registry.AddToRef<UserGrain>();
        registry.AddToRef<UserSessionGrain>();
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
        registry.AddToRef<UserTrustGrain>();
        registry.AddToRef<FeatureFlagGrain>();
        registry.AddToRef<OperatorAuthChallengeGrain>();
        registry.AddToRef<AppsManagementGrain>();
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

    public string Description => "voice control, calls, SIP";
    public bool   IsClient    => false;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<RepositoriesFeature>();
        features.Add<PermissionsFeature>();
        features.Add<SfuFeature>();
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
    {
        registry.AddToRef<VoiceControlGrain>();
        registry.AddToRef<CallGrain>();
        registry.AddToRef<SipGrain>();

        registry.AcceptRemote<IFeatureFlagGrain>("single call site in SipGrain; not worth co-hosting core's flags");
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
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<RepositoriesFeature>();
        features.Add<ContentModerationFeature>();
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
        => registry.AddToRef<ContentModerationGrain>();
}

public sealed class CommerceRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Commerce;

    public string Description => "entitlements, boosts, inventory, levels";
    public bool   IsClient    => false;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>(); 
        features.Add<RepositoriesFeature>();
        features.Add<PermissionsFeature>();
        features.Add<XsollaFeature>();
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
    {
        registry.AddToRef<UltimaGrain>();
        registry.AddToRef<SpaceBoostGrain>();
        registry.AddToRef<InventoryGrain>();
        registry.AddToRef<UserLevelGrain>();
        registry.AddToRef<EntitlementGrain>();

        registry.AcceptRemote<IUserGrain>("single call site in UltimaGrain");
    }
}

public sealed class JobsRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Jobs;

    public string Description   => "account deletion, exports, e-mail, reports";
    public bool   IsClient      => false;
    public bool   UsesReminders => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<RepositoriesFeature>();
        features.Add<TemplateEngineFeature>();
        features.Add<AccountDeletionFeature>();
        features.Add<ReportSystemFeature>();
        features.Add<NotificationsFeature>();
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
    {
        registry.AddToRef<AccountDeletionGrain>();
        registry.AddToRef<AutoDeleteSchedulerGrain>();
        registry.AddToRef<ExportPumpGrain>();
        registry.AddToRef<UserDataExportGrain>();
        registry.AddToRef<EmailManager>();
        registry.AddToRef<ReportGrain>();

        registry.AddStartupCall<IAutoDeleteSchedulerGrain>();

        registry.AcceptRemote<IFileStorageGrain>("cleanup path only; media owns the storage stack");
        registry.AcceptRemote<IUserTrustGrain>("single call site in ReportGrain");
    }
}
