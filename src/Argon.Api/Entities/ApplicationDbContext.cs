namespace Argon.Entities;

using System.Drawing;
using System.Linq.Expressions;
using Features.EF;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<User>           Users                  { get; set; }
    public DbSet<UserAgreements> UserAgreements         { get; set; }
    public DbSet<Server>         Servers                { get; set; }
    public DbSet<Channel>        Channels               { get; set; }
    public DbSet<ServerMember>   UsersToServerRelations { get; set; }

    public DbSet<ServerMemberArchetype>       ServerMemberArchetypes       { get; set; }
    public DbSet<Archetype>                   Archetypes                   { get; set; }
    public DbSet<ChannelEntitlementOverwrite> ChannelEntitlementOverwrites { get; set; }


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
           .HasConversion<Features.EF.ColorConverter>();

        modelBuilder.Entity<ServerMember>()
           .HasOne(x => x.Server)
           .WithMany(x => x.Users)
           .HasForeignKey(x => x.ServerId);

        modelBuilder.Entity<ServerMember>()
           .HasOne(x => x.User)
           .WithMany(x => x.ServerMembers)
           .HasForeignKey(x => x.UserId);

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

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ArgonEntity).IsAssignableFrom(entityType.ClrType))
                continue;
            modelBuilder.Entity(entityType.ClrType)
               .HasQueryFilter(GetSoftDeleteFilter(entityType.ClrType));
        }

        modelBuilder.Entity<User>().HasData(new User
        {
            Username       = "system",
            DisplayName    = "System",
            Email          = "system@argon.gl",
            Id             = User.SystemUser,
            PasswordDigest = null
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
    }

    private static LambdaExpression GetSoftDeleteFilter(Type type)
    {
        var parameter         = Expression.Parameter(type, "e");
        var isDeletedProperty = Expression.Property(parameter, nameof(ArgonEntity.IsDeleted));
        var notDeleted        = Expression.Not(isDeletedProperty);
        return Expression.Lambda(notDeleted, parameter);
    }
}