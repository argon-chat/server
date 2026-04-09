namespace Argon.Core.Entities.Data;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// A registered slash command for a bot application.
/// SpaceId null = global command, non-null = space-scoped command.
/// </summary>
public class BotCommandEntity : IEntityTypeConfiguration<BotCommandEntity>
{
    public Guid  CommandId { get; set; }
    public Guid  AppId     { get; set; }
    public Guid? SpaceId   { get; set; }

    public required string Name        { get; set; }
    public required string Description { get; set; }

    public List<BotCommandOption> Options { get; set; } = [];

    public bool     DefaultPermission { get; set; } = true;
    public DateTime CreatedAt         { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt         { get; set; } = DateTime.UtcNow;

    public virtual DevAppEntity App { get; set; } = null!;

    public void Configure(EntityTypeBuilder<BotCommandEntity> builder)
    {
        builder.ToTable("BotCommands");
        builder.HasKey(x => x.CommandId);

        builder.Property(x => x.Name)
           .HasMaxLength(32)
           .IsRequired();

        builder.Property(x => x.Description)
           .HasMaxLength(100)
           .IsRequired();

        builder.HasOne(x => x.App)
           .WithMany()
           .HasForeignKey(x => x.AppId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.AppId, x.SpaceId, x.Name })
           .IsUnique();

        builder.Property(x => x.Options)
           .HasColumnType("jsonb")
           .HasConversion(
                v => Newtonsoft.Json.JsonConvert.SerializeObject(v),
                v => Newtonsoft.Json.JsonConvert.DeserializeObject<List<BotCommandOption>>(v) ?? new List<BotCommandOption>());
    }
}

public sealed record BotCommandOption
{
    public required string              Name        { get; init; }
    public required string              Description { get; init; }
    public required BotCommandOptionType Type       { get; init; }
    public          bool                Required    { get; init; }
    public          List<CommandChoice>? Choices     { get; init; }
    public          List<BotCommandOption>? SubOptions { get; init; }
}

public sealed record CommandChoice
{
    public required string Name  { get; init; }
    public required object Value { get; init; }
}

public enum BotCommandOptionType
{
    String,
    Integer,
    Boolean,
    User,
    Channel,
    Role,
    Number
}
