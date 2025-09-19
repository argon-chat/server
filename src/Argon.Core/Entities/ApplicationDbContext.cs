namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Drawing;
using System.Linq.Expressions;
using Api.Entities.Data;
using static ArgonEntitlement;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity>                        Users                        => Set<UserEntity>();
    public DbSet<UserDeviceHistoryEntity>           DeviceHistories              => Set<UserDeviceHistoryEntity>();
    public DbSet<UserAgreements>                    UserAgreements               => Set<UserAgreements>();
    public DbSet<SpaceEntity>                       Servers                      => Set<SpaceEntity>();
    public DbSet<ChannelEntity>                     Channels                     => Set<ChannelEntity>();
    public DbSet<SpaceMemberEntity>                 UsersToServerRelations       => Set<SpaceMemberEntity>();
    public DbSet<SpaceMemberArchetypeEntity>        ServerMemberArchetypes       => Set<SpaceMemberArchetypeEntity>();
    public DbSet<ArchetypeEntity>                   Archetypes                   => Set<ArchetypeEntity>();
    public DbSet<ChannelEntitlementOverwriteEntity> ChannelEntitlementOverwrites => Set<ChannelEntitlementOverwriteEntity>();
    public DbSet<ServerInvite>                      ServerInvites                => Set<ServerInvite>();
    public DbSet<ArgonMessageEntity>                Messages                     => Set<ArgonMessageEntity>();
    public DbSet<UserProfileEntity>                 UserProfiles                 => Set<UserProfileEntity>();
    public DbSet<UsernameReservedEntity>            Reservation                  => Set<UsernameReservedEntity>();
    public DbSet<SpaceCategoryEntity>               Categories                   => Set<SpaceCategoryEntity>();
    public DbSet<ArgonItemEntity>                   Items                        => Set<ArgonItemEntity>();
    public DbSet<ArgonItemNotificationEntity>       UnreadInventoryItems         => Set<ArgonItemNotificationEntity>();
    public DbSet<ArgonCouponEntity>                 Coupons                      => Set<ArgonCouponEntity>();
    public DbSet<ArgonCouponRedemptionEntity>       CouponRedemption             => Set<ArgonCouponRedemptionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ArgonEntity).IsAssignableFrom(entityType.ClrType))
                continue;
            modelBuilder.Entity(entityType.ClrType)
               .HasQueryFilter(GetSoftDeleteFilter(entityType.ClrType));
        }

        modelBuilder.Entity<UserEntity>().HasData(new UserEntity
        {
            Username           = "system",
            DisplayName        = "System",
            Email              = "system@argon.gl",
            Id                 = UserEntity.SystemUser,
            PasswordDigest     = null,
            NormalizedUsername = "system"
        });

        modelBuilder.Entity<SpaceEntity>().HasData(new SpaceEntity
        {
            Name      = "system_server",
            CreatorId = UserEntity.SystemUser,
            Id        = SpaceEntity.DefaultSystemServer
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
                SpaceId       = SpaceEntity.DefaultSystemServer,
                Name          = "everyone",
                IsLocked      = false,
                IsMentionable = true,
                IsHidden      = false,
                Description   = "Default role for everyone in this server",
                CreatedAt     = new DateTime(2024, 11, 23, 16, 1, 14, 205, DateTimeKind.Utc).AddTicks(8411)
            }
        ]);

        modelBuilder.Entity<ArchetypeEntity>().HasData([
            new ArchetypeEntity
            {
                Id            = ArchetypeEntity.DefaultArchetype_Owner,
                Colour        = Color.Gray,
                CreatorId     = UserEntity.SystemUser,
                Entitlement   = ArgonEntitlementKit.Administrator,
                SpaceId       = SpaceEntity.DefaultSystemServer,
                Name          = "owner",
                IsLocked      = true,
                IsMentionable = false,
                IsHidden      = true,
                Description   = "Default role for owner in this server",
                CreatedAt     = new DateTime(2024, 11, 23, 16, 1, 14, 205, DateTimeKind.Utc).AddTicks(8382)
            }
        ]);

        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v)
        );

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                    property.SetValueConverter(dateTimeOffsetConverter);
            }
        }

        var dateTimeOffsetNullConverter = new ValueConverter<DateTimeOffset?, long?>(
            v => (v == null ? null : v.Value.ToUnixTimeMilliseconds()),
            v => v == null ? null : DateTimeOffset.FromUnixTimeMilliseconds(v.Value)
        );

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(dateTimeOffsetNullConverter);
            }
        }
    }

    private static LambdaExpression GetSoftDeleteFilter(Type type)
    {
        var parameter         = Expression.Parameter(type, "e");
        var isDeletedProperty = Expression.Property(parameter, nameof(ArgonEntity.IsDeleted));
        var notDeleted        = Expression.Not(isDeletedProperty);
        return Expression.Lambda(notDeleted, parameter);
    }
}