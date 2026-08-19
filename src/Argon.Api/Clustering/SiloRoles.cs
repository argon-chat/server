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
    /// extra machinery, and a draining silo drops out of the gateway list on its own — every provider
    /// filters on <c>Status == Active</c>.</para>
    ///
    /// <para>The cost is one extra hop for a call to a grain that lives elsewhere: entry point to a
    /// core gateway to the target silo. Those calls — commerce, media, jobs — are already doing
    /// database or object-storage work that dwarfs a hop on the same network.</para>
    /// </remarks>
    public bool ExposesClusterGateway => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
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

        // Every grain that raises an event needs the bus. Publishing goes through the backplane, so
        // this brings no listening socket with it — the client endpoint is entrypoint's business.
        features.Add<RealtimeBusFeature>();

        // SecurityGrain and AuthorizationGrain do the password and device work themselves.
        features.Add<ArgonAuthorizationFeature>();

        // ChannelGrain and SavedGifsGrain take IS3StorageService directly rather than going through
        // IFileStorageGrain, so the S3 client lands here whatever role owns the storage grain. That
        // is a leak worth closing at the call sites, not a decision about where media belongs.
        features.Add<FileStorageFeature>();
    }

    public void OnGrainReferences(IGrainCollectionRegistry registry)
    {
        registry.AddToRef<SpaceGrain>();

        // The read side of a space, hosted beside the write side so a cache miss is a local call.
        // It is a stateless worker, so this is a pool per silo rather than a single activation.
        registry.AddToRef<SpaceReadGrain>();

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

        // Space RBAC — archetypes, channel permission overwrites, member assignment — keyed by
        // spaceId. "Entitlement" here is the permission bitmask, not anything paid, and it was on
        // commerce for no better reason than the word. Beside SpaceGrain it shares the key, and the
        // client's login burst stops crossing a role boundary for it.
        registry.AddToRef<EntitlementGrain>();

        registry.AddToRef<OperatorAuthChallengeGrain>();
        registry.AddToRef<AppsManagementGrain>();

        // The read side the identity server asks about people — which account is signing in, and
        // whether it is also an operator. Beside DevTeamsGrain, which answers the same questions
        // about applications, so an OAuth authorization does not cross a role boundary twice.
        registry.AddToRef<IdentityDirectoryGrain>();
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

    public string Description => "voice control, calls, SIP";
    public bool   IsClient    => false;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<SiloLifecycleFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<RepositoriesFeature>();
        features.Add<PermissionsFeature>();
        features.Add<SfuFeature>();

        // CallGrain and SipGrain find and notify the sessions in a call, and post the "call started"
        // system message.
        features.Add<PresenceFeature>();
        features.Add<MessagesFeature>();
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
        features.Add<SiloLifecycleFeature>();
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
        features.Add<SiloLifecycleFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<RepositoriesFeature>();
        features.Add<PermissionsFeature>();
        features.Add<XsollaFeature>();

        // A purchase tells the buyer and their space about itself.
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

    public string Description   => "account deletion, exports, e-mail, reports";
    public bool   IsClient      => false;
    public bool   UsesReminders => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<SiloLifecycleFeature>();
        features.Add<SentryFeature>();
        features.Add<CacheFeature>();
        features.Add<RepositoriesFeature>();
        features.Add<TemplateEngineFeature>();
        features.Add<AccountDeletionFeature>();
        features.Add<ReportSystemFeature>();
        features.Add<NotificationsFeature>();

        // Deleting an account verifies the password and closes the sessions; exporting one writes an
        // archive to the export bucket.
        features.Add<ArgonAuthorizationFeature>();
        features.Add<PresenceFeature>();
        features.Add<FileStorageFeature>();
        features.Add<OtpFeature>();
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
        registry.AcceptRemote<IUserGrain>(
            "reached through the authorization stack the deletion path needs; co-hosting it would " +
            "put a second copy of core's busiest worker on a role that runs batch work");
    }
}
