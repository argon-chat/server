namespace Argon.Api.Grains.Interfaces;

using Contracts;

[Alias("Argon.Api.Grains.Interfaces.IAuthorizationGrain")]
public interface IAuthorizationGrain : IGrainWithStringKey
{
    [Alias("Authorize")]
    Task<Either<JwtToken, AuthorizationError>> Authorize(UserCredentialsInput input);

    public const string DefaultId = "auth";
}

[Alias("Argon.Api.Grains.Interfaces.IUserActiveSessionGrain")]
public interface IUserActiveSessionGrain : IGrainWithGuidKey
{
    [Alias("AddMachineKey")]
    ValueTask AddMachineKey(Guid issueId, string key, string region, string hostName, string platform);

    [Alias("HasKeyExist")]
    ValueTask<bool> HasKeyExist(Guid issueId);

    [Alias("Remove")]
    ValueTask Remove(Guid issueId);

    [Alias("IndicateLastActive")]
    ValueTask IndicateLastActive(Guid issueId);
}