namespace Argon.Features.Vault;

/// <summary>
/// Where the database is, and which engine is on the other end.
/// </summary>
/// <remarks>
/// This file used to also carry <c>VaultDbCredentialsProvider</c>, which leased short-lived database
/// credentials from Vault's database secret engine and rewrote the connection string with them. It is
/// gone, along with <c>UseRotationHolder</c>, <c>RotationHolderSecretEngine</c> and
/// <c>RotationHolderRoleName</c>.
/// <para>
/// It was never switched on. The flag was <c>false</c> everywhere, so <c>EnsureLoadedAsync</c>
/// returned without asking Vault for anything, the hosted service woke every five minutes to find the
/// flag still false and went back to sleep, and the connection string came from configuration exactly
/// as it does now. What the code cost was not runtime: it was a registration on every role with a
/// database, a warm-up step in <c>Program.cs</c> ahead of the one that matters, and a standing
/// suggestion that the connection string might be built at runtime — which is the kind of thing a
/// reader has to disprove before they can reason about anything downstream of it.
/// </para>
/// <para>
/// Vault itself is still reached, and by something that does work: <c>VaultPkiService</c> issues the
/// certificates behind operator step-up. Turning the auth method off to be rid of the client would
/// break that, and would break it lazily — the service resolves <c>IVaultClient</c> inside the call,
/// so nothing fails until an operator asks for a certificate.
/// </para>
/// </remarks>
public record DatabaseOptions : Argon.Features.Clustering.IValidatableFeatureOptions
{
    /// <summary>
    /// Where the database is. Falls back to <c>ConnectionStrings:Default</c>, which is where this
    /// lived before the section owned it — so an existing deployment keeps working, and a
    /// <c>conf.d/database.json</c> can now say it without reaching into someone else's section.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Which flavour of the PostgreSQL wire protocol. Unset means CockroachDB.</summary>
    public string? Provider { get; set; }

    // The pool and timeout settings below are defaults: a keyword the connection string sets itself wins.

    /// <summary>Also the size of the pooled DbContext factory.</summary>
    public int MaxPoolSize { get; set; } = 100;

    public int MinPoolSize { get; set; } = 2;

    /// <summary>How long an idle connection is kept; too short and every burst pays TCP, TLS and SCRAM again.</summary>
    public TimeSpan ConnectionIdleLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Rotates connections so they spread back over nodes that restarted.</summary>
    public TimeSpan ConnectionLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// List only the nodes of the pod's own region in <c>Host</c> when turning this on: <c>Messages</c> is
    /// <c>REGIONAL BY ROW</c> on <c>gateway_region()</c>.
    /// </summary>
    public bool LoadBalanceHosts { get; set; }

    /// <summary>Off until a load test says otherwise.</summary>
    public int MaxAutoPrepare { get; set; }

    /// <summary>Client-side, per attempt. Npgsql counts it as transient, so it is retried.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Server-side <c>statement_timeout</c>, sent through the <c>Options</c> keyword. Unset leaves the
    /// server's own. Unlike <see cref="CommandTimeout"/> a cancelled statement is not retried.
    /// </summary>
    public TimeSpan? StatementTimeout { get; set; }

    public int MaxRetryCount { get; set; } = 3;

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Statements per round trip; matches the message write buffer's batch.</summary>
    public int MaxBatchSize { get; set; } = 256;

    public void Validate(Argon.Features.Clustering.IFeatureConfigurationReport report)
    {
        report.Require(
            !string.IsNullOrWhiteSpace(ConnectionString) ||
            !string.IsNullOrWhiteSpace(report.Read<ConnectionStringsSection>("ConnectionStrings").Default),
            nameof(ConnectionString),
            "is not set and neither is ConnectionStrings:Default; there is no database to reach");

        report.RequireRange(MaxPoolSize, 1, 1024, nameof(MaxPoolSize));
        report.RequireRange(MinPoolSize, 0, MaxPoolSize, nameof(MinPoolSize));
        report.RequireRange(ConnectionIdleLifetime, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1), nameof(ConnectionIdleLifetime));
        report.RequireRange(ConnectionLifetime, TimeSpan.Zero, TimeSpan.FromDays(1), nameof(ConnectionLifetime));
        report.RequireRange(MaxAutoPrepare, 0, 1024, nameof(MaxAutoPrepare));
        report.RequireRange(CommandTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), nameof(CommandTimeout));
        report.RequireRange(MaxRetryCount, 0, 10, nameof(MaxRetryCount));
        report.RequireRange(MaxRetryDelay, TimeSpan.Zero, TimeSpan.FromMinutes(1), nameof(MaxRetryDelay));
        report.RequireRange(MaxBatchSize, 1, 10_000, nameof(MaxBatchSize));

        if (StatementTimeout is { } statement)
            report.RequireRange(statement, TimeSpan.FromMilliseconds(1), TimeSpan.FromHours(1), nameof(StatementTimeout));
    }
}

/// <summary>The shape of the framework's own <c>ConnectionStrings</c> block, for the one rule that reads it.</summary>
public sealed class ConnectionStringsSection
{
    public string? Default { get; set; }
}
