namespace Argon.Entities;

using Api.Entities.Data;
using Argon.Core.Entities.Data;
using Argon.Features.EF;
using System.Drawing;
using static ArgonEntitlement;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IOptions<DatabaseRegionOptions> regionOptions) : DbContext(options)
{
    public DbSet<UserEntity>                        Users                        => Set<UserEntity>();
    public DbSet<UserDeviceHistoryEntity>           DeviceHistories              => Set<UserDeviceHistoryEntity>();
    public DbSet<SpaceEntity>                       Spaces                       => Set<SpaceEntity>();
    public DbSet<ChannelEntity>                     Channels                     => Set<ChannelEntity>();
    public DbSet<SpaceMemberEntity>                 UsersToServerRelations       => Set<SpaceMemberEntity>();
    public DbSet<SpaceMemberArchetypeEntity>        MemberArchetypes             => Set<SpaceMemberArchetypeEntity>();
    public DbSet<ArchetypeEntity>                   Archetypes                   => Set<ArchetypeEntity>();
    public DbSet<ChannelEntitlementOverwriteEntity> ChannelEntitlementOverwrites => Set<ChannelEntitlementOverwriteEntity>();
    public DbSet<SpaceInvite>                       Invites                      => Set<SpaceInvite>();
    public DbSet<ArgonMessageEntity>                Messages                     => Set<ArgonMessageEntity>();
    public DbSet<UserProfileEntity>                 UserProfiles                 => Set<UserProfileEntity>();
    public DbSet<UsernameReservedEntity>            Reservation                  => Set<UsernameReservedEntity>();
    public DbSet<ArgonItemEntity>                   Items                        => Set<ArgonItemEntity>();
    public DbSet<ArgonItemNotificationEntity>       UnreadInventoryItems         => Set<ArgonItemNotificationEntity>();
    public DbSet<ArgonCouponEntity>                 Coupons                      => Set<ArgonCouponEntity>();
    public DbSet<ArgonCouponRedemptionEntity>       CouponRedemption             => Set<ArgonCouponRedemptionEntity>();
    public DbSet<NotificationCounterEntity>         NotificationCounters         => Set<NotificationCounterEntity>();
    public DbSet<ChannelReadStateEntity>             ChannelReadStates            => Set<ChannelReadStateEntity>();
    public DbSet<MuteSettingsEntity>                 MuteSettings                 => Set<MuteSettingsEntity>();
    public DbSet<SystemNotificationEntity>           SystemNotifications          => Set<SystemNotificationEntity>();

#region Feature Flags

    public DbSet<FeatureFlagEntity>         FeatureFlags         => Set<FeatureFlagEntity>();
    public DbSet<FeatureFlagOverrideEntity> FeatureFlagOverrides => Set<FeatureFlagOverrideEntity>();

#endregion

#region User Stats & Levels

    public DbSet<UserDailyStatsEntity> UserDailyStats => Set<UserDailyStatsEntity>();
    public DbSet<UserLevelEntity>      UserLevels     => Set<UserLevelEntity>();

#endregion

#region Security & Settings

    public DbSet<UserPasskeyEntity>           Passkeys            => Set<UserPasskeyEntity>();
    public DbSet<UserAutoDeleteSettingEntity> AutoDeleteSettings  => Set<UserAutoDeleteSettingEntity>();
    public DbSet<PendingEmailChangeEntity>    PendingEmailChanges => Set<PendingEmailChangeEntity>();
    public DbSet<PendingPhoneChangeEntity>    PendingPhoneChanges => Set<PendingPhoneChangeEntity>();

#endregion

#region Apps

    public DbSet<DevTeamEntity>       TeamEntities       => Set<DevTeamEntity>();
    public DbSet<DevAppEntity>        AppEntities        => Set<DevAppEntity>();
    public DbSet<DevTeamMemberEntity> MemberTeamEntities => Set<DevTeamMemberEntity>();
    public DbSet<BotEntity>           BotEntities        => Set<BotEntity>();
    public DbSet<ClientAppEntity>     AppClientEntities  => Set<ClientAppEntity>();
    public DbSet<DevTeamMemberInvite> TeamInvites        => Set<DevTeamMemberInvite>();
    public DbSet<BotCommandEntity>    BotCommands        => Set<BotCommandEntity>();

#endregion

#region Friends

    public DbSet<UserBlockEntity>     UserBlocklist => Set<UserBlockEntity>();

    // Which machines an account has signed in from, and which machines are barred. See
    // DeviceIdentityService for how a login is attributed to one.
    public DbSet<DeviceObservationEntity> DeviceObservations => Set<DeviceObservationEntity>();
    public DbSet<DeviceBanEntity>         DeviceBans         => Set<DeviceBanEntity>();
    public DbSet<DeviceKeyEntity>         DeviceKeys         => Set<DeviceKeyEntity>();
    public DbSet<FriendRequestEntity> FriendRequest => Set<FriendRequestEntity>();
    public DbSet<FriendshipEntity>    Friends       => Set<FriendshipEntity>();

    // Flexible "about-me" privacy rules (who may do X to/about me), e.g. "stream.draw".
    public DbSet<PrivacyRuleEntity>   PrivacyRules  => Set<PrivacyRuleEntity>();

    // Conversation-based DM system
    public DbSet<ConversationEntity>     Conversations     => Set<ConversationEntity>();
    public DbSet<DirectMessageV2Entity>  DirectMessages    => Set<DirectMessageV2Entity>();
    public DbSet<UserConversationEntity> UserConversations => Set<UserConversationEntity>();

#endregion

#region Ultima & Boosts

    public DbSet<UltimaSubscriptionEntity>  UltimaSubscriptions   => Set<UltimaSubscriptionEntity>();
    public DbSet<SpaceBoostEntity>          SpaceBoosts           => Set<SpaceBoostEntity>();
    public DbSet<PaymentTransactionEntity>  PaymentTransactions   => Set<PaymentTransactionEntity>();

#endregion

#region File Storage

    public DbSet<FileEntity>        Files        => Set<FileEntity>();
    public DbSet<FileBlobEntity>    FileBlobs    => Set<FileBlobEntity>();
    public DbSet<FileCounterEntity> FileCounters => Set<FileCounterEntity>();

#endregion

#region GIF Storage

    public DbSet<SavedGifEntity> SavedGifs => Set<SavedGifEntity>();

#endregion

#region Content Moderation

    public DbSet<ContentViolationEntity> ContentViolations => Set<ContentViolationEntity>();

#endregion

#region Reports & Trust

    public DbSet<ReportEntity>         Reports         => Set<ReportEntity>();
    public DbSet<UserTrustScoreEntity> UserTrustScores => Set<UserTrustScoreEntity>();

#endregion

#region Operators

    public DbSet<OperatorEntity> Operators => Set<OperatorEntity>();
    public DbSet<OperatorCertificateEntity> OperatorCertificates => Set<OperatorCertificateEntity>();
    public DbSet<OperatorAuditEntity> OperatorAuditLog => Set<OperatorAuditEntity>();
    public DbSet<OperatorAppAccessEntity> OperatorAppAccess => Set<OperatorAppAccessEntity>();

#endregion

#region Discovery (self-hosted / enterprise routing)

    public DbSet<TenantDirectoryEntity> TenantDirectory => Set<TenantDirectoryEntity>();

#endregion

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.Conventions.Add(_ => new DefaultStringColumnTypeConvention());

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseMultiRegionDatabase(regionOptions.Value.PrimaryRegion, regionOptions.Value.ReplicateRegion);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        modelBuilder.PlaceArgonTables();
        modelBuilder.UseUnsignedLongCompatibility();
        modelBuilder.UseSoftDeleteCompatibility();

        modelBuilder.Entity<UserEntity>().HasData(new UserEntity
        {
            Username       = "system",
            DisplayName    = "System",
            Email          = "system@argon.gl",
            Id             = UserEntity.SystemUser,
            PasswordDigest = null
        });

        modelBuilder.Entity<SpaceEntity>().HasData(new SpaceEntity
        {
            Name      = "system_server",
            CreatorId = UserEntity.SystemUser,
            Id        = SpaceEntity.DefaultSystemSpace
        });

        modelBuilder.Entity<ArchetypeEntity>().HasData([
            new ArchetypeEntity
            {
                Id        = ArchetypeEntity.DefaultArchetype_Everyone,
                Colour    = Color.Gray,
                CreatorId = UserEntity.SystemUser,
                Entitlement = ViewChannel | ReadHistory | JoinToVoice | SendMessages | SendVoice | AttachFiles | AddReactions | AnyMentions |
                              MentionEveryone | ExternalEmoji | ExternalStickers | UseCommands | PostEmbeddedLinks | Connect | Speak | Video |
                              Stream,
                SpaceId       = SpaceEntity.DefaultSystemSpace,
                Name          = "everyone",
                IsLocked      = false,
                IsMentionable = true,
                IsHidden      = false,
                Description   = "Default role for everyone in this space",
                CreatedAt     = new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc).AddTicks(0)
            }
        ]);

        modelBuilder.Entity<ArchetypeEntity>().HasData([
            new ArchetypeEntity
            {
                Id            = ArchetypeEntity.DefaultArchetype_Owner,
                Colour        = Color.Gray,
                CreatorId     = UserEntity.SystemUser,
                Entitlement   = ArgonEntitlementKit.Administrator,
                SpaceId       = SpaceEntity.DefaultSystemSpace,
                Name          = "owner",
                IsLocked      = true,
                IsMentionable = false,
                IsHidden      = true,
                Description   = "Default role for owner in this space",
                CreatedAt     = new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc).AddTicks(0)
            }
        ]);


        modelBuilder.Entity<UserEntity>().HasData(new UserEntity
        {
            Username       = "echo",
            DisplayName    = "Echo",
            Email          = "echo@argon.gl",
            Id             = UserEntity.EchoUser,
            PasswordDigest = null,
        });

        // Last, and it has to be last: this one reads the model instead of adding to it, so it only
        // covers the entity types, keys and column defaults that everything above has finished
        // declaring. It produces no DDL — see ArgonIdGeneration.
        modelBuilder.UseRegionTaggedIds();
    }
}


/// <summary>
/// Which tables are replicated everywhere and which are homed in a region.
/// </summary>
/// <remarks>
/// <para>In one place rather than beside each entity, because it only makes sense read together:
/// what is global and what is regional is one decision about the product, not eleven decisions about
/// eleven tables.</para>
///
/// <para><b>It only takes effect when a table is created.</b> The generator writes <c>LOCALITY</c> as
/// part of <c>CREATE TABLE</c> and EF produces no migration operation at all when the annotation
/// changes on an existing table — see <c>DbLocalityTests</c>, which pins that. Changing a table's
/// locality on a live database is <c>ALTER TABLE … SET LOCALITY</c> run deliberately, because it
/// moves every row.</para>
///
/// <para>Everything not named here keeps the default, which is regional by table in the primary
/// region — today's behaviour for every table. Silence is the current arrangement, not an oversight.</para>
/// </remarks>
public static class ArgonTablePlacement
{
    public static ModelBuilder PlaceArgonTables(this ModelBuilder modelBuilder)
    {
        // Replicated to every region. Small, read on every bootstrap and by every permission check,
        // written rarely — which is what a global table is fast at and what it is slow at. This is
        // what lets a user who reconnects to another region find their spaces, roles and friends
        // while a region is down.
        modelBuilder.Entity<UserEntity>().PlacementGlobal();
        modelBuilder.Entity<UserProfileEntity>().PlacementGlobal();
        modelBuilder.Entity<SpaceEntity>().PlacementGlobal();
        modelBuilder.Entity<ChannelEntity>().PlacementGlobal();
        modelBuilder.Entity<ChannelGroupEntity>().PlacementGlobal();
        modelBuilder.Entity<ArchetypeEntity>().PlacementGlobal();
        modelBuilder.Entity<SpaceMemberEntity>().PlacementGlobal();
        modelBuilder.Entity<SpaceMemberArchetypeEntity>().PlacementGlobal();
        modelBuilder.Entity<ChannelEntitlementOverwriteEntity>().PlacementGlobal();
        modelBuilder.Entity<SpaceInvite>().PlacementGlobal();

        // Homed where the row was written. Nothing carries a region column for that: Cockroach
        // defaults the hidden crdb_region to gateway_region(), and a channel's messages are only
        // ever inserted by the activation that owns the channel, which runs in the space's home
        // region. The pinning falls out of where the grain runs.
        modelBuilder.Entity<ArgonMessageEntity>().PlacementRegionalByRow();

        return modelBuilder;
    }
}
