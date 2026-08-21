namespace Argon.Features.BotApi;

/// <summary>
/// CLI commands for Bot API contract management.
/// Invoked via <c>dotnet run -- bot-api {command}</c>.
/// </summary>
public static class BotApiCli
{
    /// <summary>
    /// Returns true if the process handled a bot-api CLI command and should exit.
    /// </summary>
    public static bool TryHandleCommand(string[] args)
    {
        // Skip leading "--" (passed by `dotnet run --`)
        if (args.Length > 0 && args[0] == "--")
            args = args[1..];

        if (args.Length < 2 || !string.Equals(args[0], "bot-api", StringComparison.OrdinalIgnoreCase))
            return false;

        var command = args[1].ToLowerInvariant();

        switch (command)
        {
            case "manifest":
                RunManifest();
                return true;

            case "verify":
                RunVerify();
                return true;

            case "rehash":
                RunRehash();
                return true;

            case "docs":
                RunDocs(args);
                return true;

            case "help":
                PrintHelp();
                return true;

            default:
                Console.Error.WriteLine($"Unknown bot-api command: {command}");
                PrintHelp();
                return true;
        }
    }

    private static void RunManifest()
    {
        var manifest = BotContractVerifier.GenerateManifest();
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented,
            new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
            });

        Console.WriteLine(json);

        // Also write to file
        var outputPath = Path.Combine(AppContext.BaseDirectory, "bot-api-manifest.json");
        File.WriteAllText(outputPath, json);
        Console.Error.WriteLine($"\nManifest written to: {outputPath}");

        // Summary
        Console.Error.WriteLine($"\n--- Bot API Manifest ---");
        foreach (var iface in manifest)
        {
            var status = iface.IsStable ? "STABLE" : "DRAFT";
            var match  = iface.IsStable && iface.DeclaredHash == iface.ComputedHash ? "OK" : 
                        iface.IsStable ? "MISMATCH!" : "";
            Console.Error.WriteLine(
                $"  {iface.Name}/v{iface.Version} [{status}] hash={iface.ComputedHash[..16]}... routes={iface.Routes.Count} {match}");
        }

        // Event definitions
        var eventDefs = BotContractVerifier.DiscoverEventDefinitions();
        if (eventDefs.Count > 0)
        {
            Console.Error.WriteLine($"\n--- Event Definitions ---");
            foreach (var (type, defAttr, _, stableAttr) in eventDefs)
            {
                var hash   = BotContractVerifier.ComputeEventContractHash(type);
                var status2 = stableAttr is not null ? "STABLE" : "DRAFT";
                var match2  = stableAttr is not null && stableAttr.ContractHash == hash ? "OK" :
                             stableAttr is not null ? "MISMATCH!" : "";
                Console.Error.WriteLine(
                    $"  {defAttr.EventType} [{status2}] hash={hash[..16]}... payload={type.Name} {match2}");
            }
        }
    }

    private static void RunVerify()
    {
        var mismatches = BotContractVerifier.Verify();

        if (mismatches.Count == 0)
        {
            Console.WriteLine("All stable contracts verified OK.");
            Environment.ExitCode = 0;
        }
        else
        {
            foreach (var m in mismatches)
            {
                Console.Error.WriteLine($"MISMATCH: {m.InterfaceName}");
                Console.Error.WriteLine($"  declared: {m.DeclaredHash}");
                Console.Error.WriteLine($"  computed: {m.ComputedHash}");
            }

            Console.Error.WriteLine($"\n{mismatches.Count} contract(s) broken.");
            Environment.ExitCode = 1;
        }
    }

    private static void RunRehash()
    {
        var manifest = BotContractVerifier.GenerateManifest();
        var stableInterfaces = manifest.Where(m => m.IsStable).ToList();

        if (stableInterfaces.Count > 0)
        {
            Console.WriteLine("Computed hashes for stable interfaces:");
            Console.WriteLine("Copy these into your [StableContract(\"...\")] attributes:\n");

            foreach (var iface in stableInterfaces)
            {
                var status = iface.DeclaredHash == iface.ComputedHash ? "unchanged" : "CHANGED";
                Console.WriteLine($"  {iface.Name}/v{iface.Version}: [StableContract(\"{iface.ComputedHash}\")] ({status})");
            }
        }

        Console.WriteLine("\nAll draft interfaces (no [StableContract]):");
        foreach (var iface in manifest.Where(m => !m.IsStable))
        {
            Console.WriteLine($"  {iface.Name}/v{iface.Version}: hash={iface.ComputedHash}");
            Console.WriteLine($"    → Add [StableContract(\"{iface.ComputedHash}\")] to freeze this version");
        }

        // Event definitions
        var eventDefs = BotContractVerifier.DiscoverEventDefinitions();
        if (eventDefs.Count > 0)
        {
            Console.WriteLine("\n--- Event Definitions ---");
            foreach (var (type, defAttr, _, stableAttr) in eventDefs)
            {
                var hash = BotContractVerifier.ComputeEventContractHash(type);
                if (stableAttr is not null)
                {
                    var status = stableAttr.ContractHash == hash ? "unchanged" : "CHANGED";
                    Console.WriteLine($"  {defAttr.EventType}: [StableEventContract(\"{hash}\")] ({status})");
                }
                else
                {
                    Console.WriteLine($"  {defAttr.EventType}: hash={hash}");
                    Console.WriteLine($"    → Add [StableEventContract(\"{hash}\")] to freeze this event");
                }
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Bot API Contract Management

            Usage: dotnet run -- bot-api <command>

            Commands:
              manifest   Generate full API manifest (JSON) with types, routes, events, and hashes
              verify     Check all [StableContract] and [StableEventContract] hashes (CI-friendly, exit code 1 on fail)
              rehash     Compute and print new hashes for all stable interfaces and events
              docs       Generate docs manifest JSON for the documentation site
              help       Show this help

            Workflow:
              1. Develop your interface (IBotInterface + [BotRoute] attributes)
              2. Define events with [BotEventDefinition] on payload records
              3. Run 'bot-api manifest' to inspect the API surface
              4. When ready to freeze: add [StableContract("<hash>")] / [StableEventContract("<hash>")]
              5. CI runs 'bot-api verify' to catch accidental breaking changes
              6. If intentional change: run 'bot-api rehash' to get new hashes
              7. Run 'bot-api docs' to regenerate documentation site data
            """);
    }

    private static void RunDocs(string[] args)
    {
        var outPath = "../../docs/bot-api-docs/src/data/api-manifest.json";

        // Parse --out argument
        for (var i = 2; i < args.Length - 1; i++)
        {
            if (args[i] is "--out" or "-o")
            {
                outPath = args[i + 1];
                break;
            }
        }

        var manifest = BotContractVerifier.GenerateDocsManifest();
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented,
            new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
            });

        var fullPath = Path.GetFullPath(outPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, json);

        Console.WriteLine($"Docs manifest written to: {fullPath}");
        Console.WriteLine($"  Interfaces: {manifest.Interfaces.Count}");
        Console.WriteLine($"  Stable: {manifest.Interfaces.Count(m => m.IsStable)}");
        Console.WriteLine($"  Draft: {manifest.Interfaces.Count(m => !m.IsStable)}");
        Console.WriteLine($"  Total routes: {manifest.Interfaces.Sum(m => m.Routes.Count)}");
        Console.WriteLine($"  Intents: {manifest.Intents.Count}");
        Console.WriteLine($"  Events: {manifest.Events.Count}");
        Console.WriteLine($"  Rate limit rules: {manifest.RateLimits.Count}");
    }
}
