namespace Argon.Core.Features.Logic;

using System.Collections.Frozen;

public static class ProfilePresetValidator
{
    private static readonly FrozenSet<int> ValidBackgroundIds = new[]
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10
    }.ToFrozenSet();

    private static readonly FrozenSet<int> ValidVoiceCardEffectIds = new[]
    {
        1, 2, 3, 4, 5
    }.ToFrozenSet();

    private static readonly FrozenSet<int> ValidAvatarFrameIds = new[]
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10
    }.ToFrozenSet();

    private static readonly FrozenSet<int> ValidNickEffectIds = new[]
    {
        1, 2, 3, 4, 5
    }.ToFrozenSet();

    public static bool IsValidBackgroundId(int id) => ValidBackgroundIds.Contains(id);
    public static bool IsValidVoiceCardEffectId(int id) => ValidVoiceCardEffectIds.Contains(id);
    public static bool IsValidAvatarFrameId(int id) => ValidAvatarFrameIds.Contains(id);
    public static bool IsValidNickEffectId(int id) => ValidNickEffectIds.Contains(id);

    public static bool IsValidPresetId(int? backgroundId, int? voiceCardEffectId, int? avatarFrameId, int? nickEffectId)
    {
        if (backgroundId.HasValue && !IsValidBackgroundId(backgroundId.Value))
            return false;
        if (voiceCardEffectId.HasValue && !IsValidVoiceCardEffectId(voiceCardEffectId.Value))
            return false;
        if (avatarFrameId.HasValue && !IsValidAvatarFrameId(avatarFrameId.Value))
            return false;
        if (nickEffectId.HasValue && !IsValidNickEffectId(nickEffectId.Value))
            return false;
        return true;
    }
}
