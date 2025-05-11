namespace Argon.Grains.Interfaces;

using Users;

[Alias("Argon.Grains.Interfaces.IUserGrain")]
public interface IUserGrain : IGrainWithGuidKey
{
    [Alias(nameof(UpdateUser))]
    Task<User> UpdateUser(UserEditInput input);

    [Alias(nameof(GetMe))]
    Task<User> GetMe();

    [Alias(nameof(GetMyServers))]
    Task<List<Server>> GetMyServers();

    [Alias(nameof(GetMyServersIds))]
    Task<List<Guid>> GetMyServersIds();

    [Alias(nameof(BroadcastPresenceAsync))]
    ValueTask BroadcastPresenceAsync(UserActivityPresence presence);

    [Alias(nameof(RemoveBroadcastPresenceAsync))]
    ValueTask RemoveBroadcastPresenceAsync();

    [Alias(nameof(CreateSocialBound))]
    ValueTask CreateSocialBound(SocialKind kind, string userData, string socialId);

    [Alias(nameof(GetMeSocials))]
    ValueTask<List<UserSocialIntegrationDto>> GetMeSocials();

    [Alias(nameof(DeleteSocialBoundAsync))]
    ValueTask<bool> DeleteSocialBoundAsync(string kind, Guid socialId);
}