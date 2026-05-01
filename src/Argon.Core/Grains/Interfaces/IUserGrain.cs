namespace Argon.Grains.Interfaces;


[Alias("Argon.Grains.Interfaces.IUserGrain")]
public interface IUserGrain : IGrainWithGuidKey
{
    [Alias(nameof(UpdateProfileAsync))]
    Task<Either<UpdateProfileResult, UpdateMeError>> UpdateProfileAsync(UserEditInput input, CancellationToken ct = default);

    [Alias(nameof(GetMe))]
    Task<UserEntity> GetMe();

    [Alias(nameof(GetAsArgonUser))]
    Task<ArgonUser> GetAsArgonUser();

    [Alias(nameof(GetMyProfile))]
    Task<ArgonUserProfile> GetMyProfile();

    [Alias(nameof(GetMyServers))]
    Task<List<ArgonSpaceBase>> GetMyServers();

    [Alias(nameof(GetMyServersIds))]
    Task<List<Guid>> GetMyServersIds(CancellationToken ct = default);

    [Alias(nameof(BroadcastPresenceAsync))]
    ValueTask BroadcastPresenceAsync(UserActivityPresence presence);

    [Alias(nameof(RemoveBroadcastPresenceAsync))]
    ValueTask RemoveBroadcastPresenceAsync();

    [Alias(nameof(UpdateUserDeviceHistory))]
    ValueTask UpdateUserDeviceHistory();

    [Alias(nameof(BeginUploadUserFile))]
    ValueTask<Either<BlobId, UploadFileError>> BeginUploadUserFile(UserFileKind kind, CancellationToken ct = default);

    [Alias(nameof(CompleteUploadUserFile))]
    ValueTask CompleteUploadUserFile(Guid blobId, UserFileKind kind, CancellationToken ct = default);

    [Alias(nameof(GetLimitationForUser))]
    ValueTask<LockedAuthStatus> GetLimitationForUser();

    /// <summary>
    /// Aggregates status from all active sessions and broadcasts the result to all servers.
    /// Called by UserSessionGrain when session status changes.
    /// </summary>
    [Alias(nameof(AggregateAndBroadcastStatusAsync))]
    ValueTask AggregateAndBroadcastStatusAsync(CancellationToken ct = default);

    [Alias(nameof(ResetPremiumProfileAsync))]
    ValueTask ResetPremiumProfileAsync(CancellationToken ct = default);
}


public record BlobId(Guid Id);

public record UpdateProfileResult(ArgonUser User, ArgonUserProfile Profile);

public enum UserFileKind
{
    Avatar
}