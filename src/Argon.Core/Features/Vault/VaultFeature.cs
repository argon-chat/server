namespace Argon.Features.Vault;

using Microsoft.Extensions.DependencyInjection;
using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.AuthMethods.Cert;
using VaultSharp.V1.AuthMethods.Kubernetes;
using VaultSharp.V1.AuthMethods.Token;

/// <summary>
/// Vault auth modes, selected via VAULT_AUTH_METHOD env var.
/// </summary>
public enum VaultAuthMode
{
    /// <summary>No Vault configured — IVaultClient will not be available</summary>
    None,
    /// <summary>Token from VAULT_TOKEN or VAULT_TOKEN_FILE</summary>
    Token,
    /// <summary>AppRole via VAULT_ROLE_ID + VAULT_SECRET_ID</summary>
    AppRole,
    /// <summary>Kubernetes via VAULT_K8S_ROLE + service account JWT</summary>
    Kubernetes,
    /// <summary>TLS client certificate via VAULT_CLIENT_CERT + VAULT_CLIENT_KEY</summary>
    Cert
}

public static class VaultFeature
{
    public static IServiceCollection AddVaultClient(this WebApplicationBuilder builder, VaultOptions options)
    {
        var mode = ResolveAuthMode(options);

        if (mode == VaultAuthMode.None)
            return builder.Services;

        builder.Services.AddSingleton<IVaultClient>(CreateVaultClient(mode, options));

        return builder.Services;
    }

    private static Func<IServiceProvider, IVaultClient> CreateVaultClient(VaultAuthMode mode, VaultOptions options)
    {
        // capture nothing that has to be resolved now — the auth material is read on first use
        return _ =>
        {
            var settings = new VaultClientSettings(options.ResolvedAddress, ResolveAuthMethodInfo(mode))
            {
                UseVaultTokenHeaderInsteadOfAuthorizationHeader = true,
                Namespace                                      = options.ResolvedNamespace
            };
            return new VaultClient(settings);
        };
    }

    /// <summary>
    /// The auth method in force: what was configured, else what the environment names, else what the
    /// presence of an address implies.
    /// </summary>
    /// <exception cref="NotSupportedException">The name is not one of the modes.</exception>
    public static VaultAuthMode ResolveAuthMode(VaultOptions options)
    {
        var raw = options.ResolvedAuthMethod;

        if (string.IsNullOrEmpty(raw))
        {
            // Auto-detect: an address, from either source, means token auth; nothing at all means
            // Vault is simply not part of this deployment.
            return options.Address is not null || Environment.GetEnvironmentVariable("VAULT_ADDR") is not null
                ? VaultAuthMode.Token
                : VaultAuthMode.None;
        }

        return Enum.TryParse<VaultAuthMode>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : throw new NotSupportedException($"Unsupported Vault auth method: {raw}");
    }

    /// <summary>
    /// Environment variables the chosen mode needs and does not have. Empty when the mode can
    /// authenticate. Lets the configuration rule report a mode that cannot work before anything tries
    /// to read a secret with it.
    /// </summary>
    public static IReadOnlyList<string> MissingMaterial(VaultAuthMode mode)
    {
        static bool Absent(params string[] names)
            => names.All(n => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n)));

        return mode switch
        {
            VaultAuthMode.Token when Absent("VAULT_TOKEN", "VAULT_TOKEN_FILE") => ["VAULT_TOKEN or VAULT_TOKEN_FILE"],
            VaultAuthMode.AppRole    => Missing("VAULT_ROLE_ID", "VAULT_SECRET_ID"),
            VaultAuthMode.Kubernetes => Missing("VAULT_K8S_ROLE"),
            VaultAuthMode.Cert       => Missing("VAULT_CLIENT_CERT", "VAULT_CLIENT_KEY"),
            _                        => []
        };

        static IReadOnlyList<string> Missing(params string[] names)
            => names.Where(n => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n))).ToArray();
    }

    private static IAuthMethodInfo ResolveAuthMethodInfo(VaultAuthMode mode) => mode switch
    {
        VaultAuthMode.Token      => ResolveToken(),
        VaultAuthMode.AppRole    => ResolveAppRole(),
        VaultAuthMode.Kubernetes => ResolveKubernetes(),
        VaultAuthMode.Cert       => ResolveCert(),
        _                        => throw new NotSupportedException($"Unsupported Vault auth method: {mode}")
    };

    private static TokenAuthMethodInfo ResolveToken()
    {
        var token = Environment.GetEnvironmentVariable("VAULT_TOKEN")
                    ?? ReadTokenFile()
                    ?? throw new InvalidOperationException(
                        "Token auth requires VAULT_TOKEN or VAULT_TOKEN_FILE environment variable");
        return new TokenAuthMethodInfo(token);
    }

    private static AppRoleAuthMethodInfo ResolveAppRole()
    {
        var roleId = Environment.GetEnvironmentVariable("VAULT_ROLE_ID")
                     ?? throw new InvalidOperationException("AppRole auth requires VAULT_ROLE_ID");
        var secretId = Environment.GetEnvironmentVariable("VAULT_SECRET_ID")
                       ?? throw new InvalidOperationException("AppRole auth requires VAULT_SECRET_ID");
        var mountPoint = Environment.GetEnvironmentVariable("VAULT_APPROLE_MOUNT") ?? "approle";
        return new AppRoleAuthMethodInfo(mountPoint, roleId, secretId);
    }

    private static KubernetesAuthMethodInfo ResolveKubernetes()
    {
        var role = Environment.GetEnvironmentVariable("VAULT_K8S_ROLE")
                   ?? throw new InvalidOperationException("Kubernetes auth requires VAULT_K8S_ROLE");
        var jwtPath = Environment.GetEnvironmentVariable("VAULT_K8S_TOKEN_PATH")
                      ?? "/var/run/secrets/kubernetes.io/serviceaccount/token";
        var jwt = File.ReadAllText(jwtPath).Trim();
        var mountPoint = Environment.GetEnvironmentVariable("VAULT_K8S_MOUNT") ?? "kubernetes";
        return new KubernetesAuthMethodInfo(mountPoint, role, jwt);
    }

    private static CertAuthMethodInfo ResolveCert()
    {
        var certPath = Environment.GetEnvironmentVariable("VAULT_CLIENT_CERT")
                       ?? throw new InvalidOperationException("Cert auth requires VAULT_CLIENT_CERT");
        var keyPath = Environment.GetEnvironmentVariable("VAULT_CLIENT_KEY")
                      ?? throw new InvalidOperationException("Cert auth requires VAULT_CLIENT_KEY");
        var roleName = Environment.GetEnvironmentVariable("VAULT_CERT_ROLE") ?? "argon";

        var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certPath, keyPath);
        return new CertAuthMethodInfo(cert, roleName);
    }

    private static string? ReadTokenFile()
    {
        var path = Environment.GetEnvironmentVariable("VAULT_TOKEN_FILE");
        if (path is null) return null;
        return File.ReadAllText(path).TrimEnd();
    }
}