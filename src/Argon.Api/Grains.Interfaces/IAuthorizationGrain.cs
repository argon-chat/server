namespace Argon.Api.Grains.Interfaces;

using Contracts;

[Alias("Argon.Api.Grains.Interfaces.IAuthorizationGrain")]
public interface IAuthorizationGrain : IGrainWithStringKey
{
    [Alias("Authorize")]
    Task<Either<JwtToken, AuthorizationError>> Authorize(UserCredentialsInput input, UserConnectionInfo connectionInfo);

    public const string DefaultId = "auth";
}

[Alias("Argon.Api.Grains.Interfaces.IUserMachineSessions")]
public interface IUserMachineSessions : IGrainWithGuidKey
{
    [Alias("CreateMachineKey")]
    ValueTask<Guid> CreateMachineKey(UserConnectionInfo connectionInfo);

    [Alias("HasKeyExist")]
    ValueTask<bool> HasKeyExist(Guid issueId);

    [Alias("Remove")]
    ValueTask Remove(Guid issueId);

    [Alias("GetAllSessions")]
    ValueTask<List<UserSessionMachineEntity>> GetAllSessions();

    [Alias("IndicateLastActive")]
    ValueTask IndicateLastActive(Guid issueId);
}