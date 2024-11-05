namespace Argon.Api.Grains.Interfaces;

using Entities;
using MemoryPack;

[Alias("Argon.Api.Grains.Interfaces.ISessionManager")]
public interface ISessionManager : IGrainWithGuidKey
{
    [Alias("Authorize")]
    Task<JwtToken> Authorize(UserCredentialsInput input);

    [Alias("GetUser")]
    Task<UserDto> GetUser();

    [Alias("Logout")]
    Task Logout(); // TODO: revoke jwt by adding it into a blacklist
}

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(JwtToken))]
public partial record struct JwtToken(string token);