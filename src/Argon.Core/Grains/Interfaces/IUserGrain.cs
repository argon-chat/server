namespace Argon.Grains.Interfaces;


[Alias("Argon.Grains.Interfaces.IUserGrain")]
public interface IUserGrain : IGrainWithGuidKey
{
    //[Alias(nameof(UpdateUser))]
    //Task<UserEntity> UpdateUser(UserEditInput input);

    [Alias(nameof(GetMe))]
    Task<UserEntity> GetMe();

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

    //[Alias(nameof(CreateSocialBound))]
    //ValueTask CreateSocialBound(SocialKind kind, string userData, string socialId);

    //[Alias(nameof(GetMeSocials))]
    //ValueTask<List<UserSocialIntegrationDto>> GetMeSocials();

    //[Alias(nameof(DeleteSocialBoundAsync))]
    //ValueTask<bool> DeleteSocialBoundAsync(string kind, Guid socialId);

    [Alias(nameof(UpdateUserDeviceHistory))]
    //[OneWay]
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
}


public record BlobId(Guid Id);


public enum UserFileKind
{
    Avatar,
    ProfileHeader
}