namespace Argon.Api.Grains.Interfaces;

using Entities;


[Alias("Argon.Api.Grains.Interfaces.ISessionManager")]
public interface ISessionManager : IGrainWithGuidKey
{
    [Alias("GetUser")]
    Task<UserDto> GetUser();

    [Alias("Logout")]
    Task Logout(); // TODO: revoke jwt by adding it into a blacklist
}