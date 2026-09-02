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
    // The channel's high-water mark, which used to be a column on Channels above. See
    // ChannelLastMessageEntity for why it is not one any more.
    public DbSet<ChannelLastMessageEntity>          ChannelLastMessages          => Set<ChannelLastMessageEntity>();
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
    public DbSet<ReportCaseEntity>     ReportCases     => Set<ReportCaseEntity>();
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
        modelBuilder.UseUnsignedEnumCompatibility();
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
/// what is global and what is regional is one decision about the product, not a separate decision
/// per table.</para>
///
/// <para><b>The criterion is frequency: how often one row is written over its life, against how
/// often that same row is read.</b> <c>LOCALITY GLOBAL</c> buys a local read in every region and
/// charges a commit-wait on every write — the commit timestamp is pushed past the cluster's maximum
/// clock offset, 500ms by default, and the writer waits for the wall clock to reach it. It is a wait
/// rather than a lock, so throughput survives and per-operation latency does not, which is why
/// Cockroach documents GLOBAL for read-mostly reference data. A row inserted once and then read by
/// every permission check for the rest of its life is that shape. A row carrying a counter, or one
/// rewritten by a background job, is not.</para>
///
/// <para><b>Count commits, not rows.</b> The wait is paid once per commit, so a hundred rows updated
/// in one <c>SaveChanges</c> costs one wait and two rows updated in two <c>SaveChanges</c> costs
/// two. That matters more than it sounds. "A reorder rewrites every sibling in a burst" reads like a
/// disqualification and is not one, because <c>SpaceGrain.MoveChannelGroup</c> mutates the whole
/// sibling set and then calls <c>SaveChangesAsync</c> exactly once (SpaceGrain:561), and
/// <c>EntitlementGrain.ReorderArchetypesAsync</c> does the identical thing (EntitlementGrain:283).
/// What genuinely costs two waits is a path that commits twice for one user action, and there is one
/// below — see <c>SpaceMemberEntity</c>.</para>
///
/// <para><b>What this replaced, and why the old rule stays retired.</b> This block used to ask "is
/// it written by something a person just clicked", and six tables were demoted at once on that
/// answer. The rule separates nothing — creating a space and editing a profile are also clicks, and
/// both stayed global — and it got <c>Channels</c> wrong, demoting a row of ordinary metadata
/// because one column on it, <c>LastMessageId</c>, moved once per message sent. The fix that worked
/// was to move the column out and put the table back. Frequency would have said that the first time,
/// and it is what restores the four tables below whose rows are inserted once on a join and then
/// read by every permission evaluation for as long as the membership lasts.</para>
///
/// <para><b>So the argument for moving a table up is a number, and the argument for keeping one down
/// is usually a column.</b> Name the writes to one row over its life and the reads of that row per
/// day. Where a single column disqualifies an otherwise cold row — <c>Channels.LastMessageId</c>
/// then, <c>Invites.UsedCount</c> now — say so by name, because the fix is to move the column rather
/// than the table. "The read side is attractive" is still not an argument on its own: every table
/// here has an attractive read side, which is exactly why the read side never decides alone.</para>
///
/// <para><b>Regional here means the primary region, deliberately spelled without naming one.</b>
/// <c>PlacementRegional()</c> renders <c>REGIONAL BY TABLE</c>, which the server reports as
/// <c>REGIONAL BY TABLE IN PRIMARY REGION</c> — the same physical state an undeclared table has, so
/// the statement moves nothing. Naming a region literal instead
/// (<c>PlacementRegional("ru-central")</c>) would pin the table to a name that a change of primary
/// region would then have to chase. The ideal for the two tables
/// still down there is stronger — each homed in <em>its own space's</em> region — and a table-level
/// clause cannot express that while one table holds every space. That is row-level placement, and §6
/// of the design is why it is a staged operation rather than a line here.</para>
///
/// <para><b>These annotations do not reach a database by themselves.</b> The generator writes
/// <c>LOCALITY</c> only as part of <c>CREATE TABLE</c>, and EF produces no migration operation at all
/// when the annotation changes on a table that already exists — see <c>DbLocalityTests</c>, which
/// pins that. Argon's migrations predate this block, so nothing here has ever been applied to the
/// production database and editing a line changes nothing on its own. Applying it is
/// <c>SchemaDeclarations</c>' job: on the boot path, under the migration lease, after migrations, it
/// reads this model and issues <c>ALTER TABLE … SET LOCALITY</c>. Which means a wrong line here is not
/// inert — it is a cluster-wide data move that leaves on the next deploy.</para>
///
/// <para>Everything not named here keeps the default, which is regional by table in the primary
/// region — today's behaviour for every table. Silence is the current arrangement, not an oversight,
/// and the step never touches a table this block does not name.</para>
/// </remarks>
public static class ArgonTablePlacement
{
    public static ModelBuilder PlaceArgonTables(this ModelBuilder modelBuilder)
    {
        // ───── Global: rows written a handful of times ever, read on every request that renders or
        // authorises anything. Every line states its own count; if you cannot state one for a table
        // you want to add here, that is the answer.

        // Inserted at registration (ArgonAuthorizationService:350 and :449, the two sign-up paths).
        // Afterwards: display name and avatar, which the grain rate-limits to one change per ten
        // minutes (UserGrain:98); the TOTP secret set, rotated or cleared (ITotpKeyStore:54, :65,
        // :74); the premium flag flipped when a subscription activates or lapses (UltimaGrain:267,
        // :311); an admin rename, email change or lockdown; and one terminal write at deletion
        // (AccountDeletionGrain:482). Order ten writes per row over an account's whole life, and the
        // most frequent of them is monthly. Against that: read on every authentication, every
        // bootstrap, every member list, and every rendered message author.
        modelBuilder.Entity<UserEntity>().PlacementGlobal();

        // Inserted in the same SaveChanges as the user above (ArgonAuthorizationService:352, :451),
        // and written afterwards only when the person edits their own bio, status or cosmetics
        // (UserGrain:99), equips an inventory item (InventoryGrain:501), or has the premium fields
        // cleared when the subscription lapses (UltimaGrain:314). CustomStatus is the busiest column
        // on the row, and somebody who changed it every hour would still sit four orders of
        // magnitude below the message path.
        modelBuilder.Entity<UserProfileEntity>().PlacementGlobal();

        // Created once (ServerRepository:34), renamed or re-described by an owner (SpaceGrain:120),
        // deleted once (SpaceGrain:390). BoostCount and BoostLevel are counters and would normally
        // be the disqualifying column; they are not, because SpaceBoostGrain:54 writes them once per
        // boost purchased or expired, and a boost is a payment. That is the difference between a
        // counter and a hot counter — what drives it, not that it counts. Read on every bootstrap,
        // every space list, and every routing decision that needs to know where a space lives.
        modelBuilder.Entity<SpaceEntity>().PlacementGlobal();

        // Two rows cloned per space at creation (ServerRepository:101–102), then one insert per role
        // created (EntitlementGrain:108), one update per role edited (:194), one delete per role
        // removed (:247), and Order rewritten across the whole set on a reorder — in one commit
        // (:283). A space's role hierarchy is set up once and then largely left alone: tens of
        // writes per row, ever. Read by every permission evaluation, because the entitlement mask
        // lives on this row.
        modelBuilder.Entity<ArchetypeEntity>().PlacementGlobal();

        // Back here, and the reason was a change to the table rather than a change of mind about it.
        // The old audit demoted Channels for one write — LastMessageId, once per message sent — and
        // that column is now a table of its own (ChannelLastMessageEntity, regional, below).
        // Coalescing the write onto a flush timer was not enough to argue with: it made a hot write
        // less hot on a row that was still hot. Moving it off is different in kind, and what is left
        // is create, rename and move: per channel lifecycle, the same shape as SpaceEntity above.
        //
        // The condition for keeping this line is therefore narrow and checkable: no writer touches
        // Channels.LastMessageId. If one comes back, this declaration is wrong again, and moving it
        // down is the fix rather than arguing that the write is small.
        modelBuilder.Entity<ChannelEntity>().PlacementGlobal();

        // Restored. One INSERT on join (SpaceGrain:171, or ServerRepository:46 for the owner's own
        // membership) and then, for most rows, nothing ever again. There is no leave-space path in
        // the server today — the wire contract has a LeavedFromServerUser event but nothing fires it,
        // only BotEventPublisher:131 translates it — and kicking is a voice-channel operation, so the
        // only later writes are the soft delete when an account is deleted (AccountDeletionGrain:613)
        // and the hard delete when a bot is uninstalled (SpaceGrain:1123). The row carries no counter
        // and no column any ordinary path updates — read SpaceMemberEntity and see. So: one write per
        // row, two at the outside, over the entire life of a membership. A leave path arriving later
        // adds one more write per row and does not change that answer.
        //
        // The read side is not merely attractive here, it is on the message path. Every permission
        // check starts at this table (ArgonPermissionProvider.CanAccess, HybridPermissionCache:24
        // and :40), the roster read pulls every row of the space (SpaceReadGrain:305), badge
        // aggregation reads a user's whole membership set (BadgeAggregationService:90), and an
        // @everyone mention scans the space's members once per message — inline up to five thousand
        // (ChannelGrain:1298) and as an INSERT..SELECT above that (ReadStateService:170). Written
        // once; read per message.
        //
        // What this costs, written down so nobody has to rediscover it: SpaceGrain.AddMemberAsync
        // commits the member row at :178 and then GrantDefaultArchetypeTo commits the archetype row
        // at ServerRepository:79 — two transactions for one join, so two commit-waits where one
        // would do. Space creation already gets this right, wrapping four SaveChanges in a single
        // transaction (ServerRepository:22–52) and paying one wait. Folding the join's two writes
        // into one transaction halves the cost of this line and is worth doing. It is not a reason
        // to keep the table regional: regional puts every permission check in the product behind a
        // WAN read the moment a second region exists.
        modelBuilder.Entity<SpaceMemberEntity>().PlacementGlobal();

        // Restored, and it is the coldest row in this file. The entity is a pure junction — primary
        // key (SpaceMemberId, ArchetypeId), two foreign keys, no other column — so there is nothing
        // on it an UPDATE could touch. A row is inserted and, maybe, deleted, and that is the
        // complete set: two at space creation (ServerRepository:112, :122), one per join
        // (ServerRepository:77), one per role grant (EntitlementGrain:453), one delete per revoke
        // (:469), and a RemoveRange when a role is deleted (:246) which is many rows in one commit.
        //
        // It is read wherever the archetypes above are read, because it is read *with* them:
        // HybridPermissionCache:24 is a single join across UsersToServerRelations, MemberArchetypes
        // and Archetypes, and so is every other base-permission read. That is what settles it.
        // Splitting the three across two localities buys no fast path for the global one — a join is
        // as far away as its furthest table — so the previous arrangement, Archetypes global and
        // these two regional, was paying for replication it could never use. All three, or none.
        modelBuilder.Entity<SpaceMemberArchetypeEntity>().PlacementGlobal();

        // Restored, and the closest call in the file. A row is inserted the first time a channel's
        // permissions name a role or a member, updated in place when the masks change
        // (EntitlementGrain:329 and :394 — one call carries the complete allow/deny pair, not one
        // call per checkbox), and deleted when the overwrite is removed (:355). Per row that is a
        // moderator configuring a channel, so single digits, and there is no automated writer at
        // all. Read on every channel access evaluation: cached alongside the channel itself in the
        // space read cache, and read uncached by HybridPermissionCache.GetChannelWithOverwritesAsync.
        //
        // Borderline, and it went up because the argument that keeps ArchetypeEntity up is the same
        // one: a mask edited by a moderator and read by every evaluation. Erring the other way meant
        // treating two halves of one permission decision differently. The condition that would flip
        // it is checkable and worth watching — the API takes the whole mask per (channel, subject)
        // pair, so a client that saved per checkbox rather than per dialog would turn one dialog
        // into a dozen commit-waits. If the client ever does that, this line is wrong.
        modelBuilder.Entity<ChannelEntitlementOverwriteEntity>().PlacementGlobal();

        // Restored, and this is where frequency most directly overrules the old audit. The objection
        // was that reordering is drag-and-drop and rewrites siblings in a burst — but the burst is a
        // burst of rows, not of commits. MoveChannelGroup mutates the moved group, lets
        // RebalanceGroupOrder rewrite the whole sibling set in the same tracked context
        // (SpaceGrain:895–904), and then calls SaveChangesAsync once (SpaceGrain:561): one
        // transaction, one commit-wait, whatever the row count. And if that were not so, the same
        // objection would take ArchetypeEntity down with it, because ReorderArchetypesAsync does
        // exactly this to Order.
        //
        // What is left per row: created once (SpaceGrain:436), renamed rarely (:473), reordered
        // occasionally, deleted once (:603) — and every one of those requires ManageChannels, so
        // this is a moderator's table rather than a member's. IsCollapsed is not written at all
        // today; the ion surface passes null for it (ChannelInteractionImpl:54). A space has a
        // handful of groups and every bootstrap reads all of them (SpaceReadGrain:323).
        modelBuilder.Entity<ChannelGroupEntity>().PlacementGlobal();

        // ───── Regional: homed in the primary region, because a column on the row is hot. Not
        // because a person clicked something — every table above is written by a person too.

        // The other half of the Channels split, and the reverse of every argument for it. One row
        // per channel, written once per flush per active channel and carrying every message since
        // the last flush: the highest-frequency write in the product after Messages itself. Every
        // reader already knows which space, and therefore which region, it is asking about — badge
        // aggregation asks by the user's spaces, the space snapshot by one space, the admin card by
        // one. There is nothing to replicate and nothing that would read it from elsewhere. Global
        // would buy a read nobody makes and charge a commit-wait on the message path, which is the
        // single most expensive place in the product to put one.
        modelBuilder.Entity<ChannelLastMessageEntity>().PlacementRegional();

        // The one-hot-column case, and worth naming as such because the shape is exactly
        // Channels-before-the-split. The row is cold: minted once with its code, space, expiry, cap
        // and optional channel (ServerInviteGrain:14), then never edited. Except UsedCount, which
        // InviteGrain:32–34 increments once per accepted join through a guarded conditional update,
        // WHERE MaxUses = 0 OR UsedCount < MaxUses. Bounded by MaxUses when MaxUses is set, and
        // unbounded when it is zero — which is what a public community link is — so the ceiling on
        // that one column is "how popular did this link get".
        //
        // The fix is therefore the same fix as Channels: move the counter, not the table. Until
        // somebody does, this stays regional and the read side loses, because invite resolution
        // genuinely does want to answer from whatever region the person following the link landed in
        // (InviteGrain.PreviewAsync). Note what is *not* the reason: GLOBAL would not have broken
        // the compare-and-swap, since a global table is still one authoritative range. The
        // commit-wait on the join path decides this, not the CAS.
        //
        // Second reason, and the only table here it applies to: this row has a TTL
        // (SpaceInvite.Configure), so a background job deletes expired invites in batches of five
        // thousand, daily, whether or not anybody touched them. That is a writer no user action
        // triggers and no read benefits from.
        modelBuilder.Entity<SpaceInvite>().PlacementRegional();

        // ───── Row-level.

        // Homed where the row was written. Nothing carries a region column for that: Cockroach
        // defaults the hidden crdb_region to gateway_region(), and a channel's messages are only
        // ever inserted by the activation that owns the channel, which runs in the space's home
        // region. The pinning falls out of where the grain runs.
        //
        // Untouched by both audits, and the most expensive line in the file to act on: converting a
        // populated Messages table is an ALTER PRIMARY KEY that rewrites every index, and the plain
        // ALTER stamps every historical row with the primary region rather than the region it was
        // actually written in. §6 covers the staged version; do not shorten it to an edit here.
        modelBuilder.Entity<ArgonMessageEntity>().PlacementRegionalByRow();

        return modelBuilder;
    }
}
