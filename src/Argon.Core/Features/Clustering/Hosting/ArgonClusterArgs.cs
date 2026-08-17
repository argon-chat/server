namespace Argon.Features.Clustering;

/// <summary>
/// Parsed clustering command line. There is deliberately no <c>ARGON_ROLE</c>/<c>ARGON_MODE</c>
/// fallback: the role system replaces the environment-variable topology outright, so a process
/// started without <c>--role</c> is a configuration error rather than something to guess at.
/// </summary>
public sealed class ArgonClusterArgs
{
    public ArgonRoleId? Role         { get; private init; }
    public string?      Topology     { get; private init; }
    public string?      Explain      { get; private init; }
    public string       GraphFormat  { get; private init; } = "dot";

    public bool Validate  { get; private init; }
    public bool ListRoles { get; private init; }
    public bool Graph     { get; private init; }
    public bool Help      { get; private init; }

    /// <summary>True when the arguments describe a diagnostic command rather than a server start.</summary>
    public bool IsCommand
        => Validate || ListRoles || Graph || Help || Explain is not null;

    public static ArgonClusterArgs Parse(string[] args)
    {
        // `dotnet run --` leaves a bare separator in front.
        if (args.Length > 0 && args[0] == "--")
            args = args[1..];

        ArgonRoleId? role     = null;
        string?      topology = null;
        string?      explain  = null;
        var          format   = "dot";
        bool validate = false, listRoles = false, graph = false, help = false;

        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null;

            switch (args[i])
            {
                case "--role" when Next() is { } value:
                    role = new ArgonRoleId(value);
                    break;
                case "--topology" when Next() is { } value:
                    topology = value;
                    break;
                case "--explain" when Next() is { } value:
                    explain = value;
                    break;
                case "--format" when Next() is { } value:
                    format = value.ToLowerInvariant();
                    break;
                case "--validate":
                    validate = true;
                    break;
                case "--roles":
                    listRoles = true;
                    break;
                case "--graph":
                    graph = true;
                    break;
                case "--cluster-help":
                    help = true;
                    break;
            }
        }

        return new ArgonClusterArgs
        {
            Role        = role,
            Topology    = topology,
            Explain     = explain,
            GraphFormat = format,
            Validate    = validate,
            ListRoles   = listRoles,
            Graph       = graph,
            Help        = help
        };
    }

    public ArgonRoleId RequireRole()
        => Role ?? throw new InvalidOperationException(
               "No role selected. Start the server with --role <name>, or run --roles to list what is available.");
}
