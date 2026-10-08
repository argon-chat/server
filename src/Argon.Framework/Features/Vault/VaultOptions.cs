namespace Argon.Features.Vault;

/// <summary>
/// How to reach Vault and how to authenticate to it.
/// </summary>
/// <remarks>
/// Deliberately thin. Vault's own contract is environment variables — <c>VAULT_ADDR</c>,
/// <c>VAULT_TOKEN</c>, the file a sidecar writes — and the secret half has no business in a
/// configuration file that gets committed or mounted as a plain ConfigMap. So the secrets stay in
/// the environment and only the addressing lives here, with the environment still winning when both
/// are set.
/// <para>
/// What this buys is validation: an auth method chosen without the material it needs is caught by
/// <c>--validate-config</c> instead of at the first secret read.
/// </para>
/// </remarks>
public sealed class VaultOptions : Argon.Features.Clustering.IValidatableFeatureOptions
{
    public void Validate(Argon.Features.Clustering.IFeatureConfigurationReport report)
    {
        if (Address is not null)
            report.RequireUri(Address, nameof(Address), "http", "https");

        VaultAuthMode mode;
        try
        {
            mode = VaultFeature.ResolveAuthMode(this);
        }
        catch (NotSupportedException e)
        {
            report.Require(false, nameof(AuthMethod), e.Message);
            return;
        }

        foreach (var missing in VaultFeature.MissingMaterial(mode))
            report.Require(false, nameof(AuthMethod),
                $"is '{mode}' but {missing} is not set, so no secret can be read");
    }

    /// <summary>Falls back to <c>VAULT_ADDR</c>, then to the local dev server.</summary>
    public string? Address { get; set; }

    /// <summary>Falls back to <c>VAULT_NAMESPACE</c>. Enterprise only; usually unset.</summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Falls back to <c>VAULT_AUTH_METHOD</c>, and then to whichever material is present. Leave unset
    /// unless you need to pin it.
    /// </summary>
    public string? AuthMethod { get; set; }

    public string ResolvedAddress
        => Address ?? Environment.GetEnvironmentVariable("VAULT_ADDR") ?? "http://localhost:8200";

    public string? ResolvedNamespace
        => Namespace ?? Environment.GetEnvironmentVariable("VAULT_NAMESPACE");

    public string? ResolvedAuthMethod
        => AuthMethod ?? Environment.GetEnvironmentVariable("VAULT_AUTH_METHOD");
}
