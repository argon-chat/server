using Argon.Grains.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Argon.Grains;

public class Hello(ILogger<Hello> logger) : Grain, IHello
{
    public Task DoIt(string who)
    {
        logger.LogInformation($"Hello: {who}");
        return Task.CompletedTask;
    }
}