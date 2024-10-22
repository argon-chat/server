namespace Argon.Api.Grains;

using Interfaces;
using Orleans.Providers;
using Persistence.States;

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
        archive.State.Ints.Add(archive.State.Hellos.Count);
        await archive.WriteStateAsync();
        var message = $"Hello, {who}!";
        logger.LogInformation(message);
        return message;
    }

    [ResponseTimeout("00:00:10")]
    public Task<Dictionary<string, List<string>>> GetList()
    {
        var list1 = archive.State.Hellos;
        var list2 = archive.State.Ints.Select(i => i.ToString()).ToList();
        logger.LogInformation("Returning list of {Count} items", list1.Count);
        return Task.FromResult(new Dictionary<string, List<string>>
        {
            { "hellos", list1 },
            { "ints", list2 }
        });
    }
}