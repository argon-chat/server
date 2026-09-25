namespace Argon.Sfu;

using Livekit.Server.Sdk.Dotnet;

/// <summary>The token a broadcaster connects its radio participant with. Only used to connect, hence the short life.</summary>
public static class RadioToken
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    public const string KindAttribute      = "argon.kind";
    public const string UserAttribute      = "argon.user";
    public const string BroadcastAttribute = "argon.broadcast";
    public const string Kind               = "radio";

    public static string Create(SfuInstanceCfg sfu, Guid userId, Guid spaceId, Guid hqChannelId)
    {
        var identity = new RadioIdentity(userId).ToRawIdentity();
        var room     = new RadioRoomId(spaceId, hqChannelId).ToRawRoomId();

        return new AccessToken(sfu.ClientId, sfu.Secret)
           .WithIdentity(identity)
           .WithName(identity)
           .WithTtl(Ttl)
           .WithAttributes(new Dictionary<string, string>
            {
                [KindAttribute]      = Kind,
                [UserAttribute]      = userId.ToString(),
                [BroadcastAttribute] = hqChannelId.ToString()
            })
           .WithGrants(SfuPermission.Radio(room))
           .ToJwt();
    }
}
