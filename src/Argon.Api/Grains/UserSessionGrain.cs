namespace Argon.Api.Grains;

using Interfaces;
using Microsoft.Extensions.Logging;

public class UserActiveSessionGrain(
    [PersistentState("userSessions", "OrleansStorage")] 
    IPersistentState<UserSessionGrainState> sessionStorage,
    ILogger<IUserActiveSessionGrain> logger) : Grain, IUserActiveSessionGrain
{
    public ValueTask AddMachineKey(Guid issueId, string key, string region, string hostName, string platform)
    {
        logger.LogInformation("User '{userId}' has add machine key {key}, {region}, {hostName}", this.GetPrimaryKey(), key, region, hostName);
        sessionStorage.State.Sessions.Add(issueId, new UserSessionMachineEntity(hostName, region, key, platform)
        {
            LatestAccess = DateTimeOffset.UtcNow
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> HasKeyExist(Guid issueId)
        => new(sessionStorage.State.Sessions.ContainsKey(issueId));

    public ValueTask Remove(Guid issueId)
    {
        sessionStorage.State.Sessions.Remove(issueId);
        return ValueTask.CompletedTask;
    }

    public ValueTask IndicateLastActive(Guid issueId)
    {
        sessionStorage.State.Sessions[issueId].LatestAccess = DateTimeOffset.UtcNow;
        return ValueTask.CompletedTask;
    }

    public async override Task OnActivateAsync(CancellationToken cancellationToken) =>
        await sessionStorage.ReadStateAsync();

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) => 
        await sessionStorage.WriteStateAsync();
}

// state to pgsql!!!
[GenerateSerializer, Serializable, MemoryPackable, Alias(nameof(UserSessionGrainState))]
public partial class UserSessionGrainState
{
    public Dictionary<Guid, UserSessionMachineEntity> Sessions { get; set; } = new();
}

public record UserSessionMachineEntity(string hostName, string region, string machineKey, string platform)
{
    public DateTimeOffset LatestAccess { get; set; }
}