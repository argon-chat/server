namespace Argon.Entities;

using Argon.Features.BotApi;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.ComponentModel.DataAnnotations.Schema;

public record ArgonMessageEntity : ArgonEntityWithOwnershipNoKey, IEntityTypeConfiguration<ArgonMessageEntity>,
                                   IMapper<ArgonMessageEntity, ArgonMessage>
{
    public          long  MessageId { get; set; }
    public required Guid  SpaceId   { get; set; }
    public required Guid  ChannelId { get; set; }
    public          long? Reply     { get; set; }

    public required string Text { get; set; }

    [Column(TypeName = "jsonb")]
    public List<IMessageEntity> Entities { get; set; } = new();

    [Column(TypeName = "jsonb")]
    public List<ControlRowV1>? Controls { get; set; }

    [Column(TypeName = "jsonb")]
    public List<MessageReactionData>? Reactions { get; set; }


    public void Configure(EntityTypeBuilder<ArgonMessageEntity> builder)
    {
        builder.HasKey(m => new
        {
            m.SpaceId,
            m.ChannelId,
            m.MessageId
        });
        builder.HasIndex(m => new
            {
                m.SpaceId,
                m.ChannelId,
                m.CreatedAt
            })
           .IncludeProperties(m => new
            {
                m.Text,
                m.Entities
            });

        // Assigned by the application, from the snowflake generator, so that a message can be given
        // its id without waiting for the insert — see PgSqlMessagesLayout.ExecuteInsertMessage and
        // SystemMessageService.SendUserJoinedMessageAsync. ValueGeneratedNever is what buys that: EF
        // sends the CLR value on every insert instead of omitting the column and reading the id back
        // out of a RETURNING clause.
        //
        // Which makes unique_rowid() below inert on every path that goes through EF, this entity's
        // own writers included. This comment used to call it "the safety net for anything that writes
        // a row without going through the message layer", and that was wrong in exactly the direction
        // that hurt: it never covered application code, which is where rows actually get written
        // without an id, and an ArgonMessageEntity constructed without a MessageId inserts 0 and
        // collides with the previous such row on the composite key. New writers must mint the id.
        //
        // It stays anyway, for two reasons. It is the default the deployed Messages table carries, so
        // dropping it here buys nothing at runtime and costs an AlterColumn migration against the
        // largest table in the schema. And it is not inert for writers that bypass EF altogether and
        // leave the column out — restores, backfills, hand-run SQL — which is the only place a column
        // default can help. Compare DirectMessageV2Entity, which is ValueGeneratedOnAdd over the same
        // default and really does let the database mint the id, paying the round trip DMs can afford.
        builder.Property(m => m.MessageId)
           .HasColumnType("BIGINT")
           .ValueGeneratedNever()
           .HasDefaultValueSql("unique_rowid()");

        builder.Property(m => m.Reply)
           .HasColumnType("BIGINT");

        builder.Property(m => m.Entities)
           .HasConversion<PolyListNewtonsoftJsonValueConverter<List<IMessageEntity>, IMessageEntity>>()
           .HasColumnType("jsonb")
           .Metadata.SetValueComparer(
                new PolyListJsonValueComparer<List<IMessageEntity>, IMessageEntity>());

        builder.Property(m => m.Controls)
           .HasColumnType("jsonb")
           .HasConversion(
                v => v == null ? null : Newtonsoft.Json.JsonConvert.SerializeObject(v),
                v => v == null ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<List<ControlRowV1>>(v))
           .Metadata.SetValueComparer(new JsonValueComparer<List<ControlRowV1>?>());

        builder.Property(m => m.Reactions)
           .HasColumnType("jsonb")
           .HasConversion(
                v => v == null ? null : Newtonsoft.Json.JsonConvert.SerializeObject(v),
                v => v == null ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<List<MessageReactionData>>(v))
           .Metadata.SetValueComparer(new JsonValueComparer<List<MessageReactionData>?>());
    }

    public const int ReactionUserPreviewLimit = 3;

    public static ArgonMessage Map(scoped in ArgonMessageEntity self)
        => new(self.MessageId, self.Reply, self.ChannelId, self.SpaceId,
            self.Text, self.Entities ?? [], self.CreatedAt.UtcDateTime, self.CreatorId,
            self.Reactions?.Select(r => new ReactionInfo(
                r.Emoji, r.CustomEmojiId, r.UserIds.Count,
                r.UserIds.Take(ReactionUserPreviewLimit).ToList())).ToList()
            ?? [],
            MapControls(self.Controls) ?? []);

    private static List<ControlRow>? MapControls(List<ControlRowV1>? rows)
    {
        if (rows is null or { Count: 0 }) return null;
        return rows.Select(row => new ControlRow(
            row.Controls.Select(c => new BotControl(
                (ArgonContracts.ControlType)(int)c.Type,
                c.Variant.HasValue ? (ArgonContracts.ButtonVariant)(int)c.Variant.Value : null,
                c.Label, c.Id, c.Url,
                c.Colour is { } col ? new ArgonContracts.OklchColor(col.L, col.C, col.H) : null,
                c.Disabled, c.CustomId, c.Placeholder,
                c.MinValues, c.MaxValues,
                c.Options?.Select(o => new SelectOption(o.Label, o.Value, o.Description, o.Default)).ToList() ?? [],
                c.RequiredArchetypeId
            )).ToList()
        )).ToList();
    }
}