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

    public void Validate(Argon.Features.Clustering.IFeatureConfigurationReport report)
        => report.Require(
            !string.IsNullOrWhiteSpace(ConnectionString) ||
            !string.IsNullOrWhiteSpace(report.Read<ConnectionStringsSection>("ConnectionStrings").Default),
            nameof(ConnectionString),
            "is not set and neither is ConnectionStrings:Default; there is no database to reach");
}

/// <summary>The shape of the framework's own <c>ConnectionStrings</c> block, for the one rule that reads it.</summary>
public sealed class ConnectionStringsSection
{
    public string? Default { get; set; }
}
