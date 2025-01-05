namespace Argon.Grains;

using Microsoft.Extensions.Logging;
using Orleans.BroadcastChannel;

public class UserMachineSessions(
    ILogger<IUserMachineSessions> logger) : Grain<UserMachineSessionGrainState>, IUserMachineSessions
{
    public async ValueTask<Guid> CreateMachineKey(UserConnectionInfo connectionInfo)
    {
        logger.LogInformation("User '{userId}' has add machine key {key}", this.GetPrimaryKey(), connectionInfo);
        var issueId = Guid.NewGuid();
        State.Sessions.Add(issueId, new UserSessionMachineEntity(
            issueId, 
            connectionInfo.HostName, 
            connectionInfo.Region, connectionInfo.IpAddress, connectionInfo.ClientName)
        {
            LatestAccess = DateTimeOffset.UtcNow
        });
        return issueId;
    }

    public ValueTask<bool> HasKeyExist(Guid issueId)
        => new(State.Sessions.ContainsKey(issueId));

    public ValueTask Remove(Guid issueId)
    {
        State.Sessions.Remove(issueId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<List<UserSessionMachineEntity>> GetAllSessions()
        => new(State.Sessions.Select(x => x.Value).ToList());

    public ValueTask IndicateLastActive(Guid issueId)
    {
        if (!State.Sessions.TryGetValue(issueId, out var session))
            return ValueTask.CompletedTask;
        session.LatestAccess = DateTimeOffset.UtcNow;
        return ValueTask.CompletedTask;
    }

    public async override Task OnActivateAsync(CancellationToken cancellationToken) =>
        await ReadStateAsync();

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) => 
        await WriteStateAsync();
}

// state to pgsql!!!
[GenerateSerializer, Serializable, MemoryPackable, Alias(nameof(UserMachineSessionGrainState))]
public partial class UserMachineSessionGrainState
{
    public Dictionary<Guid, UserSessionMachineEntity> Sessions { get; set; } = new();
}
[GenerateSerializer, Serializable, MemoryPackable, Alias(nameof(UserSessionMachineEntity))]
public partial record UserSessionMachineEntity(Guid id, string hostName, string region, string ipAddress, string platform)
{
    public DateTimeOffset LatestAccess { get; set; }
}