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
/// <para><b>The test is the write side, not the read side.</b> <c>LOCALITY GLOBAL</c> buys a local
/// read in every region and charges a commit-wait on <em>every</em> write: the commit timestamp is
/// pushed past the cluster's maximum clock offset — 500ms by default — and the writer waits for the
/// wall clock to reach it. It is a wait rather than a lock, so throughput survives and per-operation
/// latency does not, which is why Cockroach documents GLOBAL for read-mostly reference data and
/// nothing else. So the question each line below answers is not "is this table small" or "is it read
/// everywhere" — it is <b>is it written on a user-facing action</b>. If it is, global is wrong however
/// attractive the read side looks, and the read side gets paid for by replicating a view over NATS
/// instead. The per-table reasoning is <c>docs/architecture/table-placement-reconciler.md</c> §5b;
/// six of the original ten global declarations failed that test and moved.</para>
///
/// <para><b>Regional here means the primary region, deliberately spelled without naming one.</b>
/// <c>PlacementRegional()</c> renders <c>REGIONAL BY TABLE</c>, which the server reports as
/// <c>REGIONAL BY TABLE IN PRIMARY REGION</c> — the same physical state an undeclared table has,
/// which is what lets the reconciler compare the two and emit nothing. Naming a region literal
/// instead (<c>PlacementRegional("ru-central")</c>) would break that normalisation and pin the table
/// to a name that a change of primary region would then have to chase. The audit's ideal is stronger
/// still — each of these homed in <em>its own space's</em> region — and a table-level clause cannot
/// express that while one table holds every space. That is row-level placement, and §6 is why it is a
/// staged operation rather than a line here.</para>
///
/// <para><b>These annotations do not reach a database by themselves.</b> The generator writes
/// <c>LOCALITY</c> only as part of <c>CREATE TABLE</c>, and EF produces no migration operation at all
/// when the annotation changes on a table that already exists — see <c>DbLocalityTests</c>, which
/// pins that. Argon's migrations predate this block, so nothing here has ever been applied to the
/// production database and editing a line changes nothing on its own. Convergence is the runtime
/// reconciler's job: it reads this model, reads the server, and issues
/// <c>ALTER TABLE … SET LOCALITY</c>. Which means a wrong line here is not inert forever — it is a
/// cluster-wide data move waiting for the reconciler to be allowed to apply it.</para>
///
/// <para>Everything not named here keeps the default, which is regional by table in the primary
/// region — today's behaviour for every table. Silence is the current arrangement, not an oversight,
/// and the reconciler never touches a table this block does not name.</para>
/// </remarks>
public static class ArgonTablePlacement
{
    public static ModelBuilder PlaceArgonTables(this ModelBuilder modelBuilder)
    {
        // Global: reference data, written per lifecycle, read on every request.
        // Small, read on every bootstrap and by every permission check, written once when an account
        // or a space is created and rarely after — the read-mostly shape the feature exists for. This
        // is what lets a user who reconnects to another region find their identity and their space
        // list while a region is down.
        modelBuilder.Entity<UserEntity>().PlacementGlobal();
        modelBuilder.Entity<UserProfileEntity>().PlacementGlobal();
        modelBuilder.Entity<SpaceEntity>().PlacementGlobal();

        // Borderline, and global on the strength of its read side alone: every permission evaluation
        // reads it, and role create/edit/delete is a moderation action measured in a handful per
        // space per month. It is the one table here whose verdict is a judgement rather than a
        // measurement. If role editing ever becomes interactive-frequency — an editor that saves per
        // keystroke, bulk role tooling, anything automated — this moves down to MemberArchetypes and
        // stops being an exception. Watch it rather than assuming it.
        modelBuilder.Entity<ArchetypeEntity>().PlacementGlobal();

        // Regional: homed in the primary region, because they are written interactively.
        // Every table below this line was declared GLOBAL and every one of them is written by
        // something a person just clicked. Moving one back up needs the write named and argued away,
        // not the read side restated: the read side was never the objection.

        // Created, renamed and moved from the channel UI. It also carries LastMessageId, which was
        // written once per message sent until ChannelGrain's flush timer started coalescing it
        // (FlushLastMessageIdAsync); that coalescing is what stopped this being the most expensive
        // row in the cluster to maintain, and it is not an argument for global — the interactive
        // writes remain.
        modelBuilder.Entity<ChannelEntity>().PlacementRegional();

        // Reordering is drag-and-drop: MoveChannelGroup rewrites the moved group's FractionalIndex
        // and usually a sibling's, and RebalanceGroupOrder rewrites the whole sibling set in one
        // burst. A burst of commit-waits is precisely the interaction that feels broken to the person
        // holding the mouse button down.
        modelBuilder.Entity<ChannelGroupEntity>().PlacementRegional();

        // One insert per join, one soft-delete per leave (SpaceGrain.AddMemberAsync) — a user-facing
        // action, and at scale the most common one in the product. The hard part is the read: a
        // user's space list spans regions. That is answered by replicating a per-user (userId ->
        // spaceId, region) index over NATS, which is small, derived, and tolerant of being 200ms
        // stale because the bootstrap then fetches each space from its own region anyway. Authority
        // for "is this user a member" stays with the space's region.
        modelBuilder.Entity<SpaceMemberEntity>().PlacementRegional();

        // One row per role grant, one delete per revoke, bursty and interactive (EntitlementGrain's
        // archetype assignment, SpaceGrain's member-archetype writes). The read side is replicated
        // for rendering only: a revoked role still honoured for 200ms in another region is a security
        // bug rather than a latency bug, so the permission gate reads the authoritative row and a
        // replica is never allowed to admit an action. InvalidateMemberPermissions already publishes
        // the invalidation this needs.
        modelBuilder.Entity<SpaceMemberArchetypeEntity>().PlacementRegional();

        // One write per toggle in the permissions UI — EntitlementGrain.UpsertArchetypeEntitlement-
        // ForChannel and its member and delete siblings. Same read-side rule as the archetypes above:
        // cache it for rendering, read the authority for the gate.
        modelBuilder.Entity<ChannelEntitlementOverwriteEntity>().PlacementRegional();

        // UsedCount is incremented on every accepted invite by a guarded conditional update in
        // InviteGrain — WHERE MaxUses = 0 OR UsedCount < MaxUses — which is a compare-and-swap and is
        // only correct against ONE authoritative copy. So this row is not a trade at all: regional
        // keeps the CAS working and drops the commit-wait from the join path. It is also the only
        // declaration of this table's placement; the note beside SpaceInvite.Configure argued global
        // for invite-link resolution and lost, because resolution is a read and reads are the cheap
        // side to fix.
        modelBuilder.Entity<SpaceInvite>().PlacementRegional();

        // Homed where the row was written. Nothing carries a region column for that: Cockroach
        // defaults the hidden crdb_region to gateway_region(), and a channel's messages are only
        // ever inserted by the activation that owns the channel, which runs in the space's home
        // region. The pinning falls out of where the grain runs.
        //
        // Untouched by the audit, and the most expensive line in the file to act on: converting a
        // populated Messages table is an ALTER PRIMARY KEY that rewrites every index, and the plain
        // ALTER stamps every historical row with the primary region rather than the region it was
        // actually written in. §6 covers the staged version; do not shorten it to an edit here.
        modelBuilder.Entity<ArgonMessageEntity>().PlacementRegionalByRow();

        return modelBuilder;
    }
}
