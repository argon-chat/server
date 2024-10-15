using Argon.Grains.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Argon.Grains;

public class Hello(ILogger<Hello> logger) : Grain, IHello
{
    public Task<string> DoIt(string who)
    {
        var message = $"Hello, {who}!";
        logger.LogInformation(message);
        return Task.FromResult(message);
    }
}