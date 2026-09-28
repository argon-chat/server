namespace Argon.Features.Expressions;

using Argon.Features.Clustering;

/// <summary>Per-space quotas for stickers and custom emoji.</summary>
public sealed class ExpressionsOptions : IValidatableFeatureOptions
{
    public const string SectionName = "Expressions";

    public static readonly int[] DefaultEmojiSlotsByBoostLevel   = [60, 120, 180, 300];
    public static readonly int[] DefaultStickerSlotsByBoostLevel = [6, 18, 36, 72];

    // Empty by default because the binder appends to a populated array; empty means the defaults above.
    /// <summary>Emoji slots per space, indexed by <c>SpaceEntity.BoostLevel</c>.</summary>
    public int[] EmojiSlotsByBoostLevel { get; set; } = [];

    /// <summary>Sticker slots per space, indexed by <c>SpaceEntity.BoostLevel</c>.</summary>
    public int[] StickerSlotsByBoostLevel { get; set; } = [];

    public int PacksPerSpace            { get; set; } = 10;
    public int ItemsPerStickerPack      { get; set; } = 120;
    public int ItemsPerEmojiPack        { get; set; } = 200;
    public int MutationsPerMinute       { get; set; } = 30;
    public int MaxCustomEmojiPerMessage { get; set; } = 100;

    /// <summary>The bots' own per-space budget, kept apart so bots and people do not starve each other.</summary>
    public int BotMutationsPerMinute { get; set; } = 120;

    public IReadOnlyList<int> EffectiveEmojiSlots
        => EmojiSlotsByBoostLevel.Length > 0 ? EmojiSlotsByBoostLevel : DefaultEmojiSlotsByBoostLevel;

    public IReadOnlyList<int> EffectiveStickerSlots
        => StickerSlotsByBoostLevel.Length > 0 ? StickerSlotsByBoostLevel : DefaultStickerSlotsByBoostLevel;

    /// <summary>Levels past the table get its last entry.</summary>
    public int SlotsFor(ExpressionKind kind, int boostLevel)
    {
        var table = kind == ExpressionKind.Emoji ? EffectiveEmojiSlots : EffectiveStickerSlots;
        return table[Math.Clamp(boostLevel, 0, table.Count - 1)];
    }

    public int ItemsPerPack(ExpressionKind kind)
        => kind == ExpressionKind.Emoji ? ItemsPerEmojiPack : ItemsPerStickerPack;

    public void Validate(IFeatureConfigurationReport report)
    {
        ValidateSlots(report, EmojiSlotsByBoostLevel, nameof(EmojiSlotsByBoostLevel));
        ValidateSlots(report, StickerSlotsByBoostLevel, nameof(StickerSlotsByBoostLevel));

        report.RequireRange(PacksPerSpace, 1, 1000, nameof(PacksPerSpace));
        report.RequireRange(ItemsPerStickerPack, 1, 10_000, nameof(ItemsPerStickerPack));
        report.RequireRange(ItemsPerEmojiPack, 1, 10_000, nameof(ItemsPerEmojiPack));
        report.RequireRange(MutationsPerMinute, 1, 10_000, nameof(MutationsPerMinute));
        report.RequireRange(BotMutationsPerMinute, 1, 10_000, nameof(BotMutationsPerMinute));
        report.RequireRange(MaxCustomEmojiPerMessage, 0, 4096, nameof(MaxCustomEmojiPerMessage));
    }

    private static void ValidateSlots(IFeatureConfigurationReport report, int[] slots, string setting)
    {
        for (var i = 0; i < slots.Length; i++)
        {
            report.Require(slots[i] >= 0, setting, $"entry {i} cannot be negative");
            report.Require(i == 0 || slots[i] >= slots[i - 1], setting, "must not decrease with the boost level");
        }
    }
}
