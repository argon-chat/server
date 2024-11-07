namespace Argon.Api.Grains.Interfaces;

using Contracts;

[Alias("Argon.Api.Grains.Interfaces.IAuthorizationGrain")]
public interface IAuthorizationGrain : IGrainWithStringKey
{
    [Alias("Authorize")]
    Task<Either<JwtToken, AuthorizationError>> Authorize(UserCredentialsInput input);

    public const string DefaultId = "auth";
}

[Alias("Argon.Api.Grains.Interfaces.IUserSessionGrain")]
public interface IUserSessionGrain : IGrainWithGuidKey
{
    [Alias("AddMachineKey")]
    ValueTask AddMachineKey(Guid issueId, string key, string region, string hostName);

    [Alias("HasKeyExist")]
    ValueTask<bool> HasKeyExist(Guid issueId);

    [Alias("Remove")]
    ValueTask Remove(Guid issueId);
}