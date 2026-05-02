namespace Argon.Features.Storage;

public enum FilePurpose
{
    Avatar          = 0,
    SpaceAvatar     = 1,
    ChannelAttachment = 2,
    Emoji           = 3,
    Sticker         = 4,
    Banner          = 5,
    Video           = 6
}

public static class FilePurposeExtensions
{
    public static bool IsPublic(this FilePurpose purpose) => purpose switch
    {
        FilePurpose.Avatar      => true,
        FilePurpose.SpaceAvatar => true,
        FilePurpose.Emoji       => true,
        FilePurpose.Sticker     => true,
        FilePurpose.Banner      => true,
        _                       => false
    };

    public static string S3Prefix(this FilePurpose purpose) => purpose switch
    {
        FilePurpose.Avatar            => "avatars",
        FilePurpose.SpaceAvatar       => "avatar",
        FilePurpose.Emoji             => "emoji",
        FilePurpose.Sticker           => "stickers",
        FilePurpose.Banner            => "banner",
        FilePurpose.ChannelAttachment => "channels",
        FilePurpose.Video             => "video",
        _                             => "misc"
    };

    /// <summary>
    /// Whether this purpose is scoped to a space (true) or to a user (false).
    /// </summary>
    public static bool IsSpaceScoped(this FilePurpose purpose) => purpose switch
    {
        FilePurpose.SpaceAvatar       => true,
        FilePurpose.Banner            => true,
        FilePurpose.Emoji             => true,
        FilePurpose.Sticker           => true,
        FilePurpose.ChannelAttachment => true,
        FilePurpose.Video             => true,
        _                             => false
    };
}
