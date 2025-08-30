namespace Argon.Features.Vault;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Vault configuration extensions.
/// </summary>
public static class VaultConfigurationExtensions
{
    /// <summary>
    /// Add Vault as a configuration provider.
    /// </summary>
    /// <param name="configuration">Configuration builder instance.</param>
    /// <param name="options">Vault options provider action.</param>
    /// <param name="basePath">Base path for vault keys.</param>
    /// <param name="mountPoint">KV mounting point.</param>
    /// <param name="logger">Logger instance.</param>
    /// <returns>Instance of <see cref="IConfigurationBuilder"/>.</returns>
    public static IConfigurationBuilder AddVaultConfiguration(
        this WebApplicationBuilder builder,
        Func<VaultOptions> options,
        string basePath,
        string? mountPoint = null)
    {
        _ = options ?? throw new ArgumentNullException(nameof(options));
        var vaultOptions = options();
        builder.Configuration.Sources.Add(new VaultConfigurationSource(vaultOptions, basePath, mountPoint));
        return builder.Configuration;
    }

    /// <summary>
    /// Add Vault as a configuration provider.
    /// </summary>
    /// <param name="configuration">Configuration builder instance.</param>
    /// <param name="basePath">Base path for vault keys.</param>
    /// <param name="mountPoint">KV mounting point.</param>
    /// <param name="logger">Logger instance.</param>
    /// <returns>Instance of <see cref="IConfigurationBuilder"/>.</returns>
    public static IConfigurationBuilder AddVaultConfiguration(
        this IConfigurationBuilder configuration,
        string basePath,
        string? mountPoint = null,
        ILogger? logger = null)
    {
        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));

        if (basePath == null)
            throw new ArgumentNullException(nameof(basePath));

        var insecureOk = bool.TryParse(Environment.GetEnvironmentVariable(VaultEnvironmentVariableNames.InsecureConnection), out var insecure);

        var vaultOptions = new VaultOptions(
            Environment.GetEnvironmentVariable(VaultEnvironmentVariableNames.Address) ??
            VaultConfigurationSource.DefaultVaultUrl,
            Environment.GetEnvironmentVariable(VaultEnvironmentVariableNames.Token) ?? VaultConfigurationSource.DefaultVaultToken,
            Environment.GetEnvironmentVariable(VaultEnvironmentVariableNames.Secret),
            Environment.GetEnvironmentVariable(VaultEnvironmentVariableNames.RoleId),
            insecureOk && insecure);
        configuration.Add(new VaultConfigurationSource(vaultOptions, basePath, mountPoint));
        return configuration;
    }
}