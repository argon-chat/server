namespace Grains.Interface;

using Argon.Contracts.etc;
using MemoryPack;
using Models;
using Orleans;

public enum AuthorizationError
{
    BAD_CREDENTIALS,
    REQUIRED_OTP,
    BAD_OTP
}

[Alias("Argon.Api.Grains.Interfaces.ISessionManager")]
public interface ISessionManager : IGrainWithGuidKey
{
    [Alias("Authorize")]
    Task<Either<JwtToken, AuthorizationError>> Authorize(UserCredentialsInput input);

    [Alias("GetUser")]
    Task<UserDto> GetUser();

    [Alias("Logout")]
    Task Logout(); // TODO: revoke jwt by adding it into a blacklist
}

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(JwtToken))]
public record struct JwtToken(string token);