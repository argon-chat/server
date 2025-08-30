namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;
using Users;

[Alias("Argon.Grains.Interfaces.IUserGrain")]
public interface IUserGrain : IGrainWithGuidKey
{
    //[Alias(nameof(UpdateUser))]
    //Task<UserEntity> UpdateUser(UserEditInput input);

    [Alias(nameof(GetMe))]
    Task<UserEntity> GetMe();

    [Alias(nameof(GetMyServers))]
    Task<List<ArgonSpaceBase>> GetMyServers();

    [Alias(nameof(GetMyServersIds))]
    Task<List<Guid>> GetMyServersIds();

    [Alias(nameof(BroadcastPresenceAsync))]
    ValueTask BroadcastPresenceAsync(UserActivityPresence presence);

    [Alias(nameof(RemoveBroadcastPresenceAsync))]
    ValueTask RemoveBroadcastPresenceAsync();

    //[Alias(nameof(CreateSocialBound))]
    //ValueTask CreateSocialBound(SocialKind kind, string userData, string socialId);

    //[Alias(nameof(GetMeSocials))]
    //ValueTask<List<UserSocialIntegrationDto>> GetMeSocials();

    //[Alias(nameof(DeleteSocialBoundAsync))]
    //ValueTask<bool> DeleteSocialBoundAsync(string kind, Guid socialId);

    [Alias(nameof(UpdateUserDeviceHistory))]
    //[OneWay]
    ValueTask UpdateUserDeviceHistory();
}