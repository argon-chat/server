namespace Argon.Api.Grains;

using Interfaces;
using Microsoft.Extensions.Logging;

public class UserSessionGrain(
    [PersistentState("userSessions", "OrleansStorage")] 
    IPersistentState<UserSessionGrainState> sessionStorage,
    ILogger<IUserSessionGrain> logger) : Grain, IUserSessionGrain
{
    public ValueTask AddMachineKey(Guid issueId, string key, string region, string hostName)
    {
        logger.LogInformation("User '{userId}' has add machine key {key}, {region}, {hostName}", this.GetPrimaryKey(), key, region, hostName);
        sessionStorage.State.Sessions.Add(issueId, (hostName, region, key));
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> HasKeyExist(Guid issueId)
        => new(sessionStorage.State.Sessions.ContainsKey(issueId));

    public ValueTask Remove(Guid issueId)
    {
        sessionStorage.State.Sessions.Remove(issueId);
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
    public Dictionary<Guid, (string hostName, string region, string machineKey)> Sessions { get; set; } = new();
}