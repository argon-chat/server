namespace Argon.Grains;

using Interfaces;
using Interfaces.States;
using Microsoft.Extensions.Logging;
using Orleans.Providers;

[StorageProvider]
public sealed class Hello(
    ILogger<Hello> logger,
    [PersistentState("hellos", "OrleansStorage")]
    IPersistentState<HelloArchive> archive)
    : Grain, IHello
{
    [ResponseTimeout("00:00:10")]
    public async Task<string> Create(string who)
    {
        archive.State.Hellos.Add(who);
        await archive.WriteStateAsync();
        var message = $"Hello, {who}!";
        logger.LogInformation(message);
        return message;
    }

    [ResponseTimeout("00:00:10")]
    public Task<List<string>> GetList()
    {
        var list = archive.State.Hellos;
        logger.LogInformation("Returning list of {Count} items", list.Count);
        return Task.FromResult(list);
    }
}