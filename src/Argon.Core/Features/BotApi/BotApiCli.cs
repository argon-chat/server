namespace Argon.Features.BotApi;

/// <summary>
/// CLI commands for the Bot API's published artefacts.
/// Invoked via <c>dotnet run -- bot-api {command}</c>.
/// <para>
/// The HTTP surface is described by <c>openapi</c>, generated from the routes themselves. What is
/// left to pin by hand is the event payloads, which reach bots over SSE and so appear in no OpenAPI
/// document — <c>verify</c> and <c>rehash</c> are about those.
/// </para>
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
            case "verify":
                RunVerify();
                return true;

            case "rehash":
                RunRehash();
                return true;

            case "docs":
                RunDocs(args);
                return true;

            case "openapi":
                RunOpenApi(args);
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

    private static void RunVerify()
    {
        var mismatches = BotContractVerifier.Verify();

        if (mismatches.Count == 0)
        {
            Console.WriteLine("All pinned event contracts verified OK.");
            Environment.ExitCode = 0;
            return;
        }

        foreach (var m in mismatches)
        {
            Console.Error.WriteLine($"MISMATCH: {m.InterfaceName}");
            Console.Error.WriteLine($"  declared: {m.DeclaredHash}");
            Console.Error.WriteLine($"  computed: {m.ComputedHash}");
        }

        Console.Error.WriteLine($"\n{mismatches.Count} contract(s) broken.");
        Environment.ExitCode = 1;
    }

    private static void RunRehash()
    {
        var eventDefs = BotContractVerifier.DiscoverEventDefinitions();

        if (eventDefs.Count == 0)
        {
            Console.WriteLine("No event definitions found.");
            return;
        }

        Console.WriteLine("Event payload hashes:");

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

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Bot API artefacts

            Usage: dotnet run -- bot-api <command>

            Commands:
              openapi    Write the OpenAPI 3.0 document (--out <path>, default: the docs site)
              docs       Write the docs data OpenAPI cannot carry: intents, events, rate limits, DTOs
              verify     Check every [StableEventContract] hash (CI-friendly, exit code 1 on fail)
              rehash     Print current hashes for all event payloads
              help       Show this help

            The HTTP surface needs no pinning by hand: 'openapi' regenerates it from the routes, and
            the committed document is what a review sees change.
            """);
    }

    /// <summary>
    /// Writes the OpenAPI description of the Bot API. Generated from the endpoints that are mapped,
    /// so it needs no database, no cluster and no running server.
    /// </summary>
    private static void RunOpenApi(string[] args)
    {
        var outPath  = ParseOut(args) ?? DocsPath(Path.Combine("public", "openapi.json"));
        var json     = BotOpenApi.GenerateOfflineAsync().GetAwaiter().GetResult();
        var fullPath = Path.GetFullPath(outPath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, json);

        Console.WriteLine($"OpenAPI document written to: {fullPath}");
        Console.WriteLine($"  {json.Length:N0} bytes");
    }

    private static string? ParseOut(string[] args)
    {
        for (var i = 2; i < args.Length - 1; i++)
        {
            if (args[i] is "--out" or "-o")
                return args[i + 1];
        }

        return null;
    }

    /// <summary>
    /// A path inside the documentation submodule, found by walking up from the binary rather than
    /// from the working directory: CI invokes these from the repository root and a developer from
    /// the project directory, and both have to write the same file.
    /// </summary>
    private static string DocsPath(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "bot-api-docs");
            if (Directory.Exists(candidate))
                return Path.Combine(candidate, relative);
        }

        throw new InvalidOperationException(
            "Could not find docs/bot-api-docs above " + AppContext.BaseDirectory +
            ". Pass --out to say where the file should go.");
    }

    private static void RunDocs(string[] args)
    {
        var outPath  = ParseOut(args) ?? DocsPath(Path.Combine("src", "data", "api-manifest.json"));
        var manifest = BotContractVerifier.GenerateDocsManifest();

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented,
            new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
            });

        var fullPath = Path.GetFullPath(outPath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // "\n" whatever the host: this file is committed and diffed against, and CRLF from a
        // Windows developer would read as a change to every line of it.
        File.WriteAllText(fullPath, json.ReplaceLineEndings("\n"));

        Console.WriteLine($"Docs manifest written to: {fullPath}");
        Console.WriteLine($"  Intents: {manifest.Intents.Count}");
        Console.WriteLine($"  Events: {manifest.Events.Count}");
        Console.WriteLine($"  Rate limit rules: {manifest.RateLimits.Count}");
        Console.WriteLine($"  DTOs: {manifest.Dtos.Count}");
        Console.WriteLine("Routes are not here — run 'bot-api openapi' for those.");
    }
}
