namespace Argon.Entities;

using System.Drawing;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Shared.Servers;
using Argon.Api.Entities.Configurations;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<User>              Users                  { get; set; }
    public DbSet<UserDeviceHistory> DeviceHistories        => Set<UserDeviceHistory>();
    public DbSet<UserAgreements>    UserAgreements         { get; set; }
    public DbSet<Server>            Servers                { get; set; }
    public DbSet<Channel>           Channels               { get; set; }
    public DbSet<ServerMember>      UsersToServerRelations { get; set; }

    public DbSet<ServerMemberArchetype>       ServerMemberArchetypes       { get; set; }
    public DbSet<Archetype>                   Archetypes                   { get; set; }
    public DbSet<ChannelEntitlementOverwrite> ChannelEntitlementOverwrites { get; set; }

    public DbSet<ServerInvite> ServerInvites { get; set; }

    public DbSet<ArgonMessage>         Messages { get; set; }
    public DbSet<ArgonMessageCounters> Counters { get; set; }

    public DbSet<ArgonMessageReaction> ArgonMessageReactions { get; set; }


    public DbSet<MeetSingleInviteLink> MeetInviteLinks { get; set; }

    public DbSet<UserSocialIntegration> SocialIntegrations { get; set; }
    public DbSet<UserProfile>           UserProfiles       { get; set; }

    public DbSet<UsernameReserved> Reservation { get; set; }
    public DbSet<SpaceCategory>    Categories  { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ArgonMessageCountersTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ArgonMessageTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UsernameReservedTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ArgonMessageReactionTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ServerMemberArchetypeTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ArchetypeTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ServerMemberTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ServerInviteTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MeetSingleInviteLinkTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ChannelTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ChannelEntitlementOverwriteTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UserSocialIntegrationTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UserProfileTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UserDeviceHistoryTypeConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SpaceCategoryTypeConfiguration).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ArgonEntity).IsAssignableFrom(entityType.ClrType))
                continue;
            modelBuilder.Entity(entityType.ClrType)
               .HasQueryFilter(GetSoftDeleteFilter(entityType.ClrType));
        }

        modelBuilder.Entity<User>().HasData(new User
        {
            Username           = "system",
            DisplayName        = "System",
            Email              = "system@argon.gl",
            Id                 = User.SystemUser,
            PasswordDigest     = null,
            NormalizedUsername = "system"
        });

        modelBuilder.Entity<Server>().HasData(new Server
        {
            Name      = "system_server",
            CreatorId = User.SystemUser,
            Id        = Server.DefaultSystemServer
        });

        modelBuilder.Entity<Archetype>().HasData([
            new Archetype
            {
                Id            = Archetype.DefaultArchetype_Everyone,
                Colour        = Color.Gray,
                CreatorId     = User.SystemUser,
                Entitlement   = ArgonEntitlement.BaseMember,
                ServerId      = Server.DefaultSystemServer,
                Name          = "everyone",
                IsLocked      = false,
                IsMentionable = true,
                IsHidden      = false,
                Description   = "Default role for everyone in this server",
                CreatedAt     = new DateTime(2024, 11, 23, 16, 1, 14, 205, DateTimeKind.Utc).AddTicks(8411)
            }
        ]);

        modelBuilder.Entity<Archetype>().HasData([
            new Archetype
            {
                Id            = Archetype.DefaultArchetype_Owner,
                Colour        = Color.Gray,
                CreatorId     = User.SystemUser,
                Entitlement   = ArgonEntitlement.Administrator,
                ServerId      = Server.DefaultSystemServer,
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