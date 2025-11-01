namespace Argon.Entities;

using Api.Entities.Data;
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

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.Conventions.Add(_ => new DefaultStringColumnTypeConvention());

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseMultiRegionDatabase(regionOptions.Value.PrimaryRegion, regionOptions.Value.ReplicateRegion);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
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
    }
}