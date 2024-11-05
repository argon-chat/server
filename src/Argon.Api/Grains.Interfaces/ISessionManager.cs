namespace Argon.Api.Grains.Interfaces;

using Entities;
using MemoryPack;

[Alias(alias: "Argon.Api.Grains.Interfaces.ISessionManager")]
public interface ISessionManager : IGrainWithGuidKey
{
    [Alias(alias: "Authorize")]
    Task<JwtToken> Authorize(UserCredentialsInput input);

    [Alias(alias: "GetUser")]
    Task<UserDto> GetUser();

    [Alias(alias: "Logout")]
    Task Logout(); // TODO: revoke jwt by adding it into a blacklist
}

[Serializable, GenerateSerializer, MemoryPackable, Alias(alias: nameof(JwtToken))]
public partial record struct JwtToken(string token);