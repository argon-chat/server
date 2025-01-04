namespace Argon.Grains.Interfaces;

using Shared.Servers;

[Alias("Argon.Grains.Interfaces.IInviteGrain")]
public interface IInviteGrain : IGrainWithStringKey
{
    [Alias("AcceptAsync")]
    ValueTask<Maybe<AcceptInviteError>> AcceptAsync(Guid userId);

    [Alias("GetAsync")]
    ValueTask<InviteCodeEntity> GetAsync();

    [Alias("HasCreatedAsync")]
    ValueTask<bool> HasCreatedAsync();

    [Alias("EnsureAsync")]
    ValueTask<InviteCode> EnsureAsync(Guid serverId, Guid issuer, TimeSpan expiration);

    [Alias("DropInviteCodeAsync")]
    ValueTask DropInviteCodeAsync();
}

[Alias("Argon.Grains.Interfaces.IServerInvitesGrain")]
public interface IServerInvitesGrain : IGrainWithGuidKey, IRemindable
{
    [Alias("CreateInviteLinkAsync")]
    Task<InviteCode> CreateInviteLinkAsync(Guid issuer, TimeSpan expiration);

    [Alias("GetInviteCodes")]
    Task<List<InviteCodeEntity>> GetInviteCodes();


    public const string StorageId = $"{nameof(IServerInvitesGrain)}";
}

[Alias("Argon.Grains.Interfaces.ServerInvitesStorage")]
public class ServerInvitesStorage
{
    public Dictionary<string, InviteCodeEntity> Entities { get; init; } = new();
}

[Alias("Argon.Grains.Interfaces.ServerInviteStorage")]
public class ServerInviteStorage
{
    public InviteCodeEntity InviteCode { get; set; } = new();
}