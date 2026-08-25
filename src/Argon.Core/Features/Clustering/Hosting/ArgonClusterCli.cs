namespace Argon.Features.Clustering;

/// <summary>
/// Diagnostic commands over the role system. Invoked before the host is built, in the same shape as
/// <c>BotApiCli.TryHandleCommand</c>.
/// </summary>
public static class ArgonClusterCli
{
    /// <summary>
    /// Validation options matching how the silos are actually configured, so <c>--validate</c>
    /// checks E4 against the storage providers the core silo configuration really registers.
    /// </summary>
    /// <summary>
    /// Dependencies whose presence in a process is the thing the role split is meant to control.
    /// Reported by <c>ARGON_DUMP_LOADED=1</c>.
    /// </summary>
    public static readonly string[] HeavyAssemblyMarkers = ["Onnx", "SixLabors"];

    public static ClusterValidationOptions DefaultValidationOptions { get; } = new()
    {
        StorageProviders = ArgonOrleansHosting.KnownStorageProviders
    };

    /// <summary>
    /// Runs a clustering command if the arguments describe one.
    /// </summary>
    /// <returns>The process exit code when a command was handled, otherwise <c>null</c> to continue booting.</returns>
    public static int? TryHandleCommand(string[] args, ClusterValidationOptions? options = null)
    {
        var parsed = ArgonClusterArgs.Parse(args);
        if (!parsed.IsCommand)
            return null;

        if (parsed.Help)
        {
            PrintHelp();
            return 0;
        }

        var catalog = ArgonClusterCatalog.Build();
        var index   = GrainTypeIndex.Build(catalog.Scope);
        var scanner = new IlGrainGraphScanner(catalog.Scope, index);

        if (parsed.ListRoles)
        {
            PrintRoles(catalog, index);
            return 0;
        }

        if (parsed.Explain is { } roleName)
            return Explain(catalog, index, scanner, new ArgonRoleId(roleName));

        if (parsed.Graph)
            return PrintGraph(catalog, index, scanner, parsed.GraphFormat);

        if (parsed.ValidateConfig)
            return ValidateConfiguration(catalog, parsed.Role);

        return RunValidation(catalog, index, scanner, parsed.Topology, options);
    }

    // ── --validate-config ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks what every enabled feature reads against the configuration this working directory would
    /// actually start with, without starting anything.
    /// </summary>
    /// <remarks>
    /// The configuration is assembled the same way the host assembles it, per-feature files included,
    /// so a <c>conf.d</c> mounted into a container can be checked by running this inside it. Roles are
    /// checked one at a time because their configuration is genuinely different — a silo has no
    /// business carrying the entry point's Kestrel settings.
    /// </remarks>
    private static int ValidateConfiguration(ArgonClusterCatalog catalog, ArgonRoleId? only)
    {
        var roles = only is { } id
            ? catalog.Find(id) is { } single ? [single] : Array.Empty<RoleDescriptor>()
            : catalog.Roles.Values.OrderBy(r => r.Id.Value, StringComparer.Ordinal).ToArray();

        if (roles.Length == 0)
        {
            Console.Error.WriteLine($"unknown role '{only}'. Known: " +
                                    string.Join(", ", catalog.Roles.Keys.Select(r => r.Value).Order()));
            return 1;
        }

        var failed = false;

        foreach (var role in roles)
        {
            var configuration = FeatureConfigurationProbe.Build(role);
            var report        = FeatureConfigurationValidator.Validate(role, configuration.Configuration);

            var sections = role.Features.Ordered.SelectMany(f => f.Options).ToArray();
            Console.WriteLine($"role '{role.Id}' — {sections.Length} configuration section(s) across " +
                              $"{role.Features.Ordered.Count} feature(s)");

            foreach (var diagnostic in configuration.Diagnostics.Concat(report.Diagnostics)
                        .OrderByDescending(d => d.Severity)
                        .ThenBy(d => d.Code, StringComparer.Ordinal)
                        .ThenBy(d => d.Message, StringComparer.Ordinal))
                Console.WriteLine($"  {diagnostic}");

            var errors = configuration.Diagnostics.Concat(report.Diagnostics)
               .Count(d => d.Severity is ClusterDiagnosticSeverity.Error);
            var warnings = configuration.Diagnostics.Concat(report.Diagnostics)
               .Count(d => d.Severity is ClusterDiagnosticSeverity.Warning);

            Console.WriteLine($"  => {errors} error(s), {warnings} warning(s)");
            Console.WriteLine();

            failed |= errors > 0;
        }

        return failed ? 1 : 0;
    }

    // ── --validate ──────────────────────────────────────────────────────────────────────────

    private static int RunValidation(
        ArgonClusterCatalog       catalog,
        GrainTypeIndex            index,
        IGrainGraphSource         scanner,
        string?                   topologyName,
        ClusterValidationOptions? options)
    {
        var targets = topologyName is null
            ? catalog.Topologies.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray()
            : catalog.Topologies.TryGetValue(topologyName, out var single) ? [single] : [];

        if (targets.Length == 0)
        {
            Console.Error.WriteLine(topologyName is null
                ? "no topologies declared; nothing to validate"
                : $"unknown topology '{topologyName}'. Known: {string.Join(", ", catalog.Topologies.Keys.Order())}");
            return 1;
        }

        var failed = false;

        foreach (var topology in targets)
        {
            var report = ClusterValidator.Validate(catalog, topology, index, scanner, options);

            Console.WriteLine($"topology '{report.Topology}' [{string.Join(", ", topology.Roles.Select(r => r.Value))}]");

            foreach (var diagnostic in report.Diagnostics
                        .OrderByDescending(d => d.Severity)
                        .ThenBy(d => d.Code, StringComparer.Ordinal)
                        .ThenBy(d => d.Message, StringComparer.Ordinal))
                Console.WriteLine($"  {diagnostic}");

            var errors   = report.Errors.Count();
            var warnings = report.Warnings.Count();
            Console.WriteLine($"  => {errors} error(s), {warnings} warning(s)");
            Console.WriteLine();

            failed |= !report.IsValid;
        }

        return failed ? 1 : 0;
    }

    // ── --roles ─────────────────────────────────────────────────────────────────────────────

    private static void PrintRoles(ArgonClusterCatalog catalog, GrainTypeIndex index)
    {
        Console.WriteLine($"{"role",-14} {"kind",-7} {"grains",6} {"features",9}  description");
        foreach (var role in catalog.Roles.Values.OrderBy(r => r.Id.Value, StringComparer.Ordinal))
            Console.WriteLine(
                $"{role.Id.Value,-14} {(role.IsClient ? "client" : "silo"),-7} " +
                $"{role.HostedGrains.Count,6} {role.Features.Ordered.Count,9}  {role.Description}");

        Console.WriteLine();
        Console.WriteLine($"{catalog.Roles.Count} role(s), {index.Classes.Count} grain class(es) discovered in " +
                          $"{catalog.Assemblies.Count} assembly(ies)");

        // The question the whole exercise turns on: what did merely discovering the roles drag in?
        // The CLR loads an assembly when something resolves a token pointing into it, so an analysis
        // pass that reads IL can undo the split without anyone noticing. ARGON_DUMP_LOADED=1 makes
        // that visible.
        if (Environment.GetEnvironmentVariable("ARGON_DUMP_LOADED") is { } dump)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            var names  = loaded.Select(a => a.GetName().Name).Where(n => n is not null).Order().ToArray();

            Console.WriteLine();

            if (dump is "all")
                foreach (var name in names)
                    Console.WriteLine($"loaded: {name}");
            else
                foreach (var heavy in names.Where(n => HeavyAssemblyMarkers.Any(
                             m => n!.Contains(m, StringComparison.OrdinalIgnoreCase))))
                    Console.WriteLine($"loaded-heavy: {heavy}");

            Console.WriteLine($"loaded-total: {loaded.Length}");
        }

        if (catalog.Topologies.Count > 0)
        {
            Console.WriteLine();
            foreach (var topology in catalog.Topologies.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
                Console.WriteLine($"topology {topology}");
        }

        foreach (var diagnostic in catalog.Diagnostics)
            Console.Error.WriteLine($"  {diagnostic}");
    }

    // ── --explain ───────────────────────────────────────────────────────────────────────────

    private static int Explain(ArgonClusterCatalog catalog, GrainTypeIndex index, IGrainGraphSource scanner, ArgonRoleId id)
    {
        if (catalog.Find(id) is not { } role)
        {
            Console.Error.WriteLine($"unknown role '{id}'. Known: {string.Join(", ", catalog.Roles.Keys.Select(r => r.Value).Order())}");
            return 1;
        }

        Console.WriteLine($"role '{role.Id}' — {(role.IsClient ? "Orleans client" : "Orleans silo")}");
        if (!string.IsNullOrWhiteSpace(role.Description))
            Console.WriteLine($"  {role.Description}");
        if (!role.IsClient)
            Console.WriteLine($"  gateway: {role.ExposesClusterGateway}, reminders: {role.UsesReminders}");

        if (role.IncludedRoles.Count > 0)
            Console.WriteLine($"\nincludes: {string.Join(", ", role.IncludedRoles.Select(t => t.Name))}");

        Console.WriteLine($"\nhosts {role.HostedGrains.Count} grain(s):");
        foreach (var grain in role.HostedGrains.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var info   = index.Info(grain);
            var badges = new List<string>();
            if (info?.IsStatelessWorker is true) badges.Add("stateless-worker");
            if (info?.IsRemindable is true) badges.Add("remindable");
            foreach (var binding in info?.StorageProviders ?? [])
                badges.Add($"storage:{binding.StorageName}");
            Console.WriteLine($"  {grain.Name}{(badges.Count > 0 ? $"  [{string.Join(", ", badges)}]" : string.Empty)}");
        }

        var roots = role.HostedGrains.Concat(role.CallRoots).Distinct().ToArray();
        var graph = roots.Length == 0 ? GrainCallGraph.Empty : scanner.Analyze(roots);
        var hosts = index.InterfacesOf(role.HostedGrains);

        var calls = graph.ByRoot.Values
           .SelectMany(s => s.Weights)
           .GroupBy(p => p.Key, p => p.Value)
           .ToDictionary(g => g.Key, g => g.Sum());
        foreach (var dynamic in role.DynamicRefs)
            calls.TryAdd(dynamic, 0);

        Console.WriteLine($"\ncalls {calls.Count} grain interface(s):");
        foreach (var (contract, weight) in calls.OrderByDescending(p => p.Value).ThenBy(p => p.Key.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {contract.Name,-34} x{weight,-4} {(hosts.Contains(contract) ? "local" : "remote")}");

        if (role.Features.Ordered.Count > 0)
        {
            Console.WriteLine($"\nfeatures, in configure order:");
            foreach (var feature in role.Features.Ordered)
            {
                var sections = feature.Options.Count == 0
                    ? string.Empty
                    : $"  [{string.Join(", ", feature.Options.Select(o => o.Section))}]";
                Console.WriteLine($"  {feature.Name}{sections}");
            }

            var configured = role.Features.Ordered.Where(f => f.Options.Count > 0).ToArray();
            if (configured.Length > 0)
            {
                Console.WriteLine($"\nreads {configured.Sum(f => f.Options.Count)} configuration section(s); " +
                                  $"each may also come from {FeatureConfigurationSources.DefaultDirectory}/<feature>.json");
            }
        }

        foreach (var site in graph.Unresolved.DistinctBy(s => (s.DeclaringType, s.MethodName)))
            Console.WriteLine($"\nunresolved: {site}");

        foreach (var diagnostic in role.Diagnostics)
            Console.Error.WriteLine($"  {diagnostic}");

        return role.Diagnostics.Any(d => d.Severity is ClusterDiagnosticSeverity.Error) ? 1 : 0;
    }

    // ── --graph ─────────────────────────────────────────────────────────────────────────────

    private static int PrintGraph(ArgonClusterCatalog catalog, GrainTypeIndex index, IGrainGraphSource scanner, string format)
    {
        var edges = new List<(string From, string To, int Weight, string Role)>();

        foreach (var role in catalog.Roles.Values.OrderBy(r => r.Id.Value, StringComparer.Ordinal))
        {
            var roots = role.HostedGrains.Concat(role.CallRoots).Distinct().ToArray();
            if (roots.Length == 0)
                continue;

            var graph = scanner.Analyze(roots);
            foreach (var (root, set) in graph.ByRoot)
            foreach (var (contract, weight) in set.Weights)
                edges.Add((root.Name, contract.Name, weight, role.Id.Value));
        }

        edges = edges.OrderByDescending(e => e.Weight).ThenBy(e => e.From, StringComparer.Ordinal).ToList();

        switch (format)
        {
            case "json":
                Console.WriteLine(JsonConvert.SerializeObject(
                    edges.Select(e => new { from = e.From, to = e.To, weight = e.Weight, role = e.Role }),
                    Formatting.Indented));
                return 0;

            case "dot":
                Console.WriteLine("digraph argon {");
                Console.WriteLine("  rankdir=LR; node [shape=box, fontname=\"monospace\"];");
                foreach (var (from, to, weight, _) in edges)
                    Console.WriteLine($"  \"{from}\" -> \"{to}\" [label=\"{weight}\"{(weight >= 4 ? ", penwidth=3" : string.Empty)}];");
                Console.WriteLine("}");
                return 0;

            default:
                Console.Error.WriteLine($"unknown --format '{format}', expected 'dot' or 'json'");
                return 1;
        }
    }

    private static void PrintHelp()
        => Console.WriteLine(
            """
            argon-server clustering commands

              --role <name>                 run the server as the named role
              --topology <name>             deployment topology this process belongs to

              --validate [--topology X]     validate all topologies, or one; exit 0 on success
              --validate-config [--role X]  check every feature's configuration; exit 0 on success
              --roles                       list declared roles and topologies
              --explain <role>              what a role hosts, calls, enables and reads
              --graph [--format dot|json]   dump the grain call graph
              --cluster-help                this text

            Configuration, lowest precedence first:
              appsettings.json, appsettings.{Environment}.json
              conf.d/<feature>.json         one file per feature, holding only that feature's sections
              $ARGON_CONFIG_FILE            one extra document, any sections
              environment variables, command line

              ARGON_CONFIG_DIR              use another directory instead of ./conf.d

            ARGON_ROLE and ARGON_MODE are not supported; the role replaces them.
            ARGON_DUMP_LOADED=1 makes --roles report which assemblies discovery pulled in, which is
            how you check that an analysis pass has not quietly undone the split.
            """);
}
