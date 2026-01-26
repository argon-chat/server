namespace Argon.Core.Entities.Data;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record DevTeamEntity : ArgonEntityNoKey, IEntityTypeConfiguration<DevTeamEntity>
{
    public Guid TeamId { get; set; }

    public Guid OwnerId { get; set; }

    public string Name { get; set; }

    public virtual UserEntity Owner { get; set; }

    public virtual ICollection<DevTeamMemberEntity> Members { get; set; } = new List<DevTeamMemberEntity>();

    public virtual ICollection<DevAppEntity> Applications { get; set; } = new List<DevAppEntity>();

    public string? AvatarFileId { get; set; }

    public void Configure(EntityTypeBuilder<DevTeamEntity> builder)
    {
        builder.ToTable("DevTeamEntity");
        builder.HasKey(x => x.TeamId);
        builder.HasOne(x => x.Owner)
           .WithMany()
           .HasForeignKey(x => x.OwnerId);

        builder.HasMany(x => x.Members)
           .WithOne(x => x.Team)
           .HasForeignKey(x => x.TeamId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Applications)
           .WithOne(x => x.Team)
           .HasForeignKey(x => x.TeamId);
    }
}

public class DevTeamMemberInvite : IEntityTypeConfiguration<DevTeamMemberInvite>
{
    public Guid TeamId     { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId   { get; set; }

    public          DateTime       CreatedAt { get; set; } = DateTime.UtcNow;
    public          bool           Accepted  { get; set; }
    public          bool           Revoked   { get; set; }
    public required DateTimeOffset ExpireAt  { get; set; }

    public virtual DevTeamEntity Team { get; set; } = null!;

    public void Configure(EntityTypeBuilder<DevTeamMemberInvite> builder)
    {
        builder.HasKey(x => new
        {
            x.TeamId,
            x.ToUserId
        });
        builder.HasIndex(x => new
        {
            x.TeamId,
            x.ToUserId
        }).IsUnique();

        builder.HasOne(x => x.Team)
           .WithMany()
           .HasForeignKey(x => x.TeamId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.ExpireAt)
           .HasColumnType("TIMESTAMPTZ")
           .IsRequired();

        builder.WithTTL(x => x.ExpireAt, CronValue.Daily,
            batchSize: 5000, rangeConcurrency: 4, deleteRateLimit: 52428800);
    }
}

public class DevTeamMemberEntity : IEntityTypeConfiguration<DevTeamMemberEntity>
{
    public         Guid          TeamId { get; set; }
    public virtual DevTeamEntity Team   { get; set; } = null!;

    public         Guid       UserId { get; set; }
    public virtual UserEntity User   { get; set; } = null!;

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;


    public bool         IsPending { get; set; }
    public bool         IsOwner   { get; set; }
    public List<string> Claims    { get; set; } = new();

    public void Configure(EntityTypeBuilder<DevTeamMemberEntity> builder)
    {
        builder.HasKey(x => new
        {
            x.TeamId,
            x.UserId
        });

        builder.HasOne(x => x.Team)
           .WithMany(x => x.Members)
           .HasForeignKey(x => x.TeamId);

        builder.HasOne(x => x.User)
           .WithMany(x => x.TeamMemberships)
           .HasForeignKey(x => x.UserId);

        builder.Property(x => x.Claims)
           .HasConversion(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<List<string>>(v) ?? new List<string>())
           .Metadata.SetValueComparer(
                new ValueComparer<List<string>>(
                    (left, right) => left.SequenceEqual(right),
                    value => value.Aggregate(0, (hash, element) => HashCode.Combine(hash, element.GetHashCode())),
                    value => value.ToList()));
        builder.Property(x => x.Claims)
           .HasColumnType("jsonb");
    }
}

public enum DevAppType
{
    Application = 0,
    Bot         = 1,
    WebApp      = 2
}

public record DevAppEntity : ArgonEntityNoKey, IEntityTypeConfiguration<DevAppEntity>
{
    [System.ComponentModel.DataAnnotations.Key]
    public Guid AppId { get; set; }

    public         Guid          TeamId { get; set; }
    public virtual DevTeamEntity Team   { get; set; } = null!;

    public          bool    IsInternalApp { get; set; }
    public required string  Name          { get; set; }
    public          string? Description   { get; set; }

    public string  ClientId        { get; set; } = null!;
    public string  ClientSecret    { get; set; } = null!;
    public string? VerificationKey { get; set; }

    public List<string> RequiredScopes   { get; set; } = new();
    public List<string> AllowedRedirects { get; set; } = new();

    public DevAppType AppType { get; set; } = DevAppType.Application;

    public void Configure(EntityTypeBuilder<DevAppEntity> builder)
    {
        builder.ToTable("DevApps");
        builder.UseTptMappingStrategy();
        builder.HasKey(x => x.AppId);

        builder.HasOne(x => x.Team)
           .WithMany(x => x.Applications)
           .HasForeignKey(x => x.TeamId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.ClientId).HasMaxLength(256);
        builder.HasIndex(x => x.ClientId).IsUnique();

        builder
           .Property(x => x.RequiredScopes)
           .HasColumnType("text[]");

        builder
           .Property(x => x.AllowedRedirects)
           .HasColumnType("text[]");
    }
}

public enum ClientAppPlatformKind
{
    WindowsDesktop,
    MacOSDesktop,
    LinuxDesktop,
    WebBased,
    iOS,
    Android
}

public record ClientAppEntity : DevAppEntity, IEntityTypeConfiguration<ClientAppEntity>
{
    public required ClientAppPlatformKind Platform           { get; set; }
    public required int                   RateLimitPerMinute { get; set; } = 60;
    public          bool                  IsVerified         { get; set; }
    public          bool                  IsPublic           { get; set; }
    public          string?               WebsiteUrl         { get; set; }
    public          string?               RepositoryUrl      { get; set; }

    public void Configure(EntityTypeBuilder<ClientAppEntity> builder)
    {
        builder.ToTable("ClientApps");
        builder.Property(x => x.Platform)
           .IsRequired();
    }
}

public record BotEntity : DevAppEntity, IEntityTypeConfiguration<BotEntity>
{
    public required string BotToken { get; set; }

    public bool RequiresOAuth2 { get; set; } = true;
    public bool IsPublic       { get; set; }
    public bool AllowDMs       { get; set; }
    public bool IsVerified     { get; set; }
    public bool IsRestricted   { get; set; }

    public int MaxSpaces { get; set; }

    public         Guid       BotAsUserId { get; set; }
    public virtual UserEntity BotAsUser   { get; set; } = null!;

    public void Configure(EntityTypeBuilder<BotEntity> builder)
    {
        builder.ToTable("Bots");
        builder.Property(x => x.BotToken)
           .IsRequired();

        builder.HasOne(x => x.BotAsUser)
           .WithOne(x => x.BotEntity)
           .HasForeignKey<BotEntity>(x => x.BotAsUserId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}