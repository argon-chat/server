using Argon.Load.Scenarios;

// Points at a server that is already running. It does not start one: the whole question is how a
// real deployment behaves, and a host started in-process would answer about TestServer instead.

var target  = Argument("--target", "http://localhost:5002");
var clients = int.Parse(Argument("--clients", "50"));

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(
        """
        argon-load — load scenarios against a running Argon server

          --scenario login-storm      what to run (default: login-storm)
          --target   <url>            server base address (default: http://localhost:5002)
          --clients  <n>              virtual users (default: 50)
          --mode     <m>              legacy | snapshot | returning (default: legacy)
          --messages <n>              fanout only: messages to send when no --rate (default: 20)
          --rate     <n>              fanout only: total messages/second; turns on sustained mode
          --senders  <n>              fanout only: how many of the listeners send (default: 1)
          --seconds  <n>              fanout only: how long to sustain --rate (default: 30)
          --channels <n>              fanout only: channels to spread senders over (default: 10)
          --listeners <n>             fanout only: how many clients hold a hub connection (default: all)

        Scenarios

          login-storm   N clients sign in at the same moment and each runs the desktop client's
                        bootstrap. --mode picks which bootstrap:

                          legacy      four calls per space, as shipped clients make them
                          snapshot    one versioned call, by a client holding nothing
                          returning   one versioned call, by a client holding the space already —
                                      the case a real client is in on every sign-in after the first

                        'returning' is the one to watch. It answers whether telling a client that
                        nothing moved is cheaper than telling it what it already knows.

          signup        N clients create an account at the same moment. Registration is the one
                        path that spends real CPU — it hashes a password — so this is the number
                        that moves when the hashing algorithm does.

          fanout        N clients hold a live hub connection to one space and some of them send.
                        Measures send-to-arrival for every (message, recipient) pair — the steady
                        state, where the client barely calls the server and lives off the stream.

                        Without --rate it sends --messages one at a time and reports latency.
                        With --rate it holds that rate across --senders for --seconds and reports
                        whether the rate was achieved; a room that cannot keep up shows lovely
                        latencies for the few messages it managed, so read the achieved line first.

        Run it twice and compare: once against --role dev (everything in one process) and once
        against a distributed deployment. The difference is what the role split costs on the path a
        person actually waits on.
        """);
    return 0;
}

using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    switch (Argument("--scenario", "login-storm"))
    {
        case "login-storm":
            if (!Enum.TryParse<BootstrapMode>(Argument("--mode", "legacy"), ignoreCase: true, out var mode))
            {
                Console.Error.WriteLine($"unknown mode '{Argument("--mode", "")}'; try --help");
                return 1;
            }

            await new LoginStorm(new Uri(target), clients, mode).RunAsync(cancellation.Token);
            return 0;

        case "fanout":
            await new FanOut(new Uri(target), clients,
                    messages: int.Parse(Argument("--messages", "20")),
                    senders: int.Parse(Argument("--senders", "1")),
                    rate: double.Parse(Argument("--rate", "0")),
                    seconds: int.Parse(Argument("--seconds", "30")),
                    channels: int.Parse(Argument("--channels", "10")),
                    listeners: int.Parse(Argument("--listeners", clients.ToString())))
               .RunAsync(cancellation.Token);
            return 0;

        case "signup":
            await new SignupRush(new Uri(target), clients).RunAsync(cancellation.Token);
            return 0;

        default:
            Console.Error.WriteLine($"unknown scenario '{Argument("--scenario", "")}'; try --help");
            return 1;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled");
    return 130;
}

string Argument(string name, string fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
