using Argon.Orleans.Client;
using Microsoft.Extensions.Hosting;

namespace ConsoleApp1;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .CreateOrleansClient()
            .Build();

        await host.StartAsync();

        var client = host.OrleansClient();

        var result = await client.SayHello();

        Console.WriteLine(result);
        Console.WriteLine(result);
        Console.WriteLine(result);
        Console.WriteLine(result);
        Console.WriteLine(result);
        Console.WriteLine(result);
        Console.WriteLine(result);
        Console.WriteLine(result);
        Console.WriteLine(result);
        Console.WriteLine(result);
        Console.WriteLine(result);

        await host.StopAsync();
    }
}