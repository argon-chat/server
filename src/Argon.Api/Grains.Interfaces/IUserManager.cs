namespace Argon.Api.Grains.Interfaces;

public interface IUserManager : IGrainWithGuidKey
{
    [Alias("CreateUser")]
    Task CreateUser(UserCredentialsInput input);

    [Alias("UpdateUser")]
    Task UpdateUser(UserCredentialsInput input);

    [Alias("DeleteUser")]
    Task DeleteUser();

    [Alias("GetUser")]
    Task GetUser();
}