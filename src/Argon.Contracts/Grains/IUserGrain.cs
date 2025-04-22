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
}