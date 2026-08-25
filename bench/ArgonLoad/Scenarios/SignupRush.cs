namespace Argon.Load.Scenarios;

using Argon.Load.Client;
using Argon.Load.Harness;
using ArgonContracts;

/// <summary>
/// Scenario C — how many people can create an account at once.
/// </summary>
/// <remarks>
/// Registration is the one path where the server deliberately does expensive work: it hashes a
/// password, and a password hash is expensive on purpose. Everything else in a request budget is
/// measured in microseconds of CPU and milliseconds of waiting; this is milliseconds of CPU, and CPU
/// does not queue politely — it takes the cores away from every other request on the node.
/// <para>
/// That makes this the number to know before changing the hashing algorithm, and the number to take
/// again afterwards. A cost-hardened hash is supposed to be slow; the question is only whether what
/// it costs is what was intended.
/// </para>
/// </remarks>
public sealed class SignupRush(Uri target, int clients)
{
    private readonly Measurement registration = new("Registration");

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"target {target}, {clients} account(s) created at once");

        var wall = await Herd.RunAsync(clients,
            prepare: (_, _) => Task.FromResult(new LoadClient(target)),
            run: RegisterAsync,
            ct);

        Report.Print($"signup rush — {clients} accounts at once", wall, [registration]);

        var created = clients - registration.Failed;

        Console.WriteLine();

        // Successes over wall clock, not clients over wall clock. The first version divided by the
        // client count and cheerfully reported a rate for a run where the server had died and every
        // single registration failed.
        Console.WriteLine($"{created / wall.TotalSeconds:0.#} registration(s) per second " +
                          $"({created} created, {registration.Failed} failed)");
        Console.WriteLine(
            "This is a CPU number, not a waiting one. If it does not move when the client count does,");
        Console.WriteLine(
            "the node is hashing as fast as it can and the rest of the request budget is behind it.");
    }

    private async Task RegisterAsync(LoadClient client, CancellationToken ct)
    {
        try
        {
            await registration.TimeAsync(async () => await SpaceFixture.RegisterAsync(client, "signup", ct));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  registration failed: {e.Message}");
        }
        finally
        {
            client.Dispose();
        }
    }
}
