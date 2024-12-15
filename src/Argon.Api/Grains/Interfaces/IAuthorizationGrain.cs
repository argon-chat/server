namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

[Alias("Argon.Grains.Interfaces.IAuthorizationGrain"), Unordered]
public interface IAuthorizationGrain : IGrainWithGuidKey
{
    [Alias("Authorize"), AlwaysInterleave]
    Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input, UserConnectionInfo connectionInfo);

    [Alias("Register"), AlwaysInterleave]
    Task<Maybe<RegistrationError>> Register(NewUserCredentialsInput input, UserConnectionInfo connectionInfo);
}

[Alias("Argon.Grains.Interfaces.IUserMachineSessions")]
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