namespace Argon.Entities;

using Features.EF;
using System.Drawing;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Newtonsoft.Json;
using Shared.Servers;

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
        modelBuilder.Entity<ArgonMessageCounters>()
           .ToTable("ArgonMessages_Counters")
           .HasKey(x => new
            {
                x.ChannelId,
                x.ServerId
            });

        modelBuilder.Entity<ArgonMessage>()
           .HasKey(m => new
            {
                m.ServerId,
                m.ChannelId,
                m.MessageId
            });

        modelBuilder.Entity<ArgonMessage>()
           .HasIndex(m => new
            {
                m.ServerId,
                m.ChannelId,
                m.MessageId
            })
           .IsUnique();


        modelBuilder.Entity<ArgonMessage>()
           .Property(m => m.MessageId);

        modelBuilder.Entity<ArgonMessage>()
           .Property(m => m.Entities)
           .HasConversion<PolyListNewtonsoftJsonValueConverter<List<MessageEntity>, MessageEntity>>()
           .HasColumnType("jsonb");

        modelBuilder.Entity<UsernameReserved>()
           .HasIndex(x => x.NormalizedUserName)
           .IsUnique();

        //modelBuilder.Entity<ArgonMessage>()
        //   .Property(x => x.CreatedAt)
        //   .HasColumnType("timestamp with time zone")
        //   .HasConversion(
        //        v => v,
        //        v => DateTime.SpecifyKind(v, DateTimeKind.Utc)
        //    );

        modelBuilder.Entity<ArgonMessageReaction>()
           .HasKey(r => new
            {
                r.ServerId,
                r.ChannelId,
                r.MessageId,
                r.UserId,
                r.Reaction
            });

        modelBuilder.Entity<ArgonMessageReaction>()
           .HasIndex(r => new
            {
                r.ServerId,
                r.ChannelId,
                r.MessageId
            })
           .IsUnique();

        modelBuilder.Entity<ServerMemberArchetype>()
           .HasKey(x => new
            {
                x.ServerMemberId,
                x.ArchetypeId
            });

        modelBuilder.Entity<ServerMemberArchetype>()
           .HasOne(x => x.ServerMember)
           .WithMany(x => x.ServerMemberArchetypes)
           .HasForeignKey(x => x.ServerMemberId);

        modelBuilder.Entity<ServerMemberArchetype>()
           .HasOne(x => x.Archetype)
           .WithMany(x => x.ServerMemberRoles)
           .HasForeignKey(x => x.ArchetypeId);

        modelBuilder.Entity<Archetype>()
           .HasOne(x => x.Server)
           .WithMany(x => x.Archetypes)
           .HasForeignKey(x => x.ServerId);

        modelBuilder.Entity<Archetype>()
           .Property(x => x.Colour)
           .HasConversion<ColourConverter>();

        modelBuilder.Entity<ServerMember>()
           .HasOne(x => x.Server)
           .WithMany(x => x.Users)
           .HasForeignKey(x => x.ServerId);

        modelBuilder.Entity<ServerMember>()
           .HasOne(x => x.User)
           .WithMany(x => x.ServerMembers)
           .HasForeignKey(x => x.UserId);

        modelBuilder.Entity<ServerInvite>()
           .HasOne(c => c.Server)
           .WithMany(s => s.ServerInvites)
           .HasForeignKey(c => c.ServerId);

        modelBuilder.Entity<ServerInvite>()
           .HasKey(x => x.Id);

        modelBuilder.Entity<MeetSingleInviteLink>()
           .HasKey(x => x.Id);

        modelBuilder.Entity<Channel>()
           .HasOne(c => c.Server)
           .WithMany(s => s.Channels)
           .HasForeignKey(c => c.ServerId);

        modelBuilder.Entity<Channel>()
           .HasIndex(x => new
            {
                x.Id,
                x.ServerId
            });

        modelBuilder.Entity<ChannelEntitlementOverwrite>()
           .HasOne(cpo => cpo.Channel)
           .WithMany(c => c.EntitlementOverwrites)
           .HasForeignKey(cpo => cpo.ChannelId);

        modelBuilder.Entity<ChannelEntitlementOverwrite>()
           .HasOne(cpo => cpo.Archetype)
           .WithMany()
           .HasForeignKey(cpo => cpo.ArchetypeId)
           .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ChannelEntitlementOverwrite>()
           .HasOne(cpo => cpo.ServerMember)
           .WithMany()
           .HasForeignKey(cpo => cpo.ServerMemberId)
           .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<UserSocialIntegration>()
           .HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserSocialIntegration>()
           .HasIndex(x => x.SocialId);

        modelBuilder.Entity<UserProfile>()
           .Property(u => u.Badges)
           .HasColumnType("jsonb")
           .HasConversion(
                v => JsonConvert.SerializeObject(v ?? new List<string>()),
                v => JsonConvert.DeserializeObject<List<string>>(v) ?? new List<string>()
            );
        modelBuilder.Entity<UserProfile>()
           .HasOne(x => x.User)
           .WithOne(x => x.Profile)
           .HasForeignKey<UserProfile>(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserDeviceHistory>()
           .HasKey(udh => new { udh.UserId, udh.MachineId });

        modelBuilder.Entity<UserDeviceHistory>()
           .HasOne(udh => udh.User)
           .WithMany()
           .HasForeignKey(udh => udh.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserDeviceHistory>()
           .Property(udh => udh.MachineId)
           .IsRequired()
           .HasMaxLength(64);

        modelBuilder.Entity<UserDeviceHistory>()
           .Property(udh => udh.LastKnownIP)
           .HasMaxLength(64);

        modelBuilder.Entity<UserDeviceHistory>()
           .Property(udh => udh.RegionAddress)
           .HasMaxLength(64);

        modelBuilder.Entity<UserDeviceHistory>()
           .Property(udh => udh.AppId)
           .HasMaxLength(64);

        modelBuilder.Entity<SpaceCategory>()
           .HasOne(x => x.Server)
           .WithMany(x => x.SpaceCategories)
           .HasForeignKey(x => x.ServerId)
           .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SpaceCategory>()
           .HasMany(x => x.Channels)
           .WithOne(x => x.Category)
           .HasForeignKey(x => x.CategoryId)
           .OnDelete(DeleteBehavior.Cascade);

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