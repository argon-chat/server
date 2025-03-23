namespace Argon.Features.Vault;

using Serilog;
using VaultSharp.V1.SecretsEngines;

/// <summary>
/// Vault configuration source.
/// </summary>
public sealed class VaultConfigurationSource : IConfigurationSource
{
    /// <summary>
    /// Default Vault URL.
    /// </summary>
    internal const string DefaultVaultUrl = "http://locahost:8200";

    /// <summary>
    /// Default Vault token.
    /// </summary>
    internal const string DefaultVaultToken = "root";

    /// <summary>
    /// Initializes a new instance of the <see cref="VaultConfigurationSource"/> class.
    /// </summary>
    /// <param name="options">Vault options.</param>
    /// <param name="basePath">Base path.</param>
    /// <param name="mountPoint">Mounting point.</param>
    public VaultConfigurationSource(VaultOptions options, string basePath, string? mountPoint = null, ILogger? logger = null)
    {
        this.Options    = options;
        this.BasePath   = basePath;
        this.MountPoint = mountPoint ?? SecretsEngineMountPoints.Defaults.KeyValueV2;
        Log.Logger.Information($"ctor VaultConfigurationSource");
    }

    /// <summary>
    /// Gets Vault connection options.
    /// </summary>
    public VaultOptions Options { get; }

    /// <summary>
    /// Gets base path for vault URLs.
    /// </summary>
    public string BasePath { get; }

    /// <summary>
    /// Gets mounting point.
    /// </summary>
    public string MountPoint { get; }

    /// <summary>
    /// Build configuration provider.
    /// </summary>
    /// <param name="builder">Configuration builder.</param>
    /// <returns>Instance of <see cref="IConfigurationProvider"/>.</returns>
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        Log.Logger.Information($"ctor VaultConfigurationSource:IConfigurationProvider:Build");

        return new VaultConfigurationProvider(this);
    }
}