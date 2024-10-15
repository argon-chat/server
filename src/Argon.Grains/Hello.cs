using Argon.Grains.Interfaces;
using Orleans;

namespace Argon.Grains;

public class Hello : Grain, IHello
{
    public  Task DoIt(string who)
    {
        Console.WriteLine($"Hello: {who}");
        return Task.CompletedTask;
    }
}