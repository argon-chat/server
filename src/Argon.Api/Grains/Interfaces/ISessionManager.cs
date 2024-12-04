namespace Argon.Grains.Interfaces;

[Alias("Argon.Grains.Interfaces.ISessionManager")]
public interface ISessionManager : IGrainWithGuidKey
{
    [Alias("GetUser")]
    Task<User> GetUser();

    [Alias("Logout")]
    Task Logout(); // TODO: revoke jwt by adding it into a blacklist
}