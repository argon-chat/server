namespace Argon.Features.Vault;

using VaultSharp;

public record struct DbCredentials(string username, string password, int ttl);

public interface IVaultDbCredentialsProvider : IHostedService
{
    Task<DbCredentials> GetCredentialsAsync();
    string              BuildConnectionString();
    Task                EnsureLoadedAsync();
    bool                IsEnabled { get; }
}

public record DatabaseOptions : Argon.Features.Clustering.IValidatableFeatureOptions
{
    /// <summary>
    /// Where the database is. Falls back to <c>ConnectionStrings:Default</c>, which is where this
    /// lived before the section owned it — so an existing deployment keeps working, and a
    /// <c>conf.d/database.json</c> can now say it without reaching into someone else's section.
    /// </summary>
    public string? ConnectionString  { get; set; }

    public bool UseRotationHolder { get; set; }

    public string? RotationHolderSecretEngine { get; set; }
    public string? RotationHolderRoleName     { get; set; }

    /// <summary>Which flavour of the PostgreSQL wire protocol. Unset means CockroachDB.</summary>
    public string? Provider { get; set; }

    public void Validate(Argon.Features.Clustering.IFeatureConfigurationReport report)
    {
        report.Require(
            !string.IsNullOrWhiteSpace(ConnectionString) ||
            !string.IsNullOrWhiteSpace(report.Read<ConnectionStringsSection>("ConnectionStrings").Default),
            nameof(ConnectionString),
            "is not set and neither is ConnectionStrings:Default; there is no database to reach");

        if (UseRotationHolder)
        {
            report.Required(RotationHolderSecretEngine, nameof(RotationHolderSecretEngine));
            report.Required(RotationHolderRoleName, nameof(RotationHolderRoleName));
        }
    }
}

/// <summary>The shape of the framework's own <c>ConnectionStrings</c> block, for the one rule that reads it.</summary>
public sealed class ConnectionStringsSection
{
    public string? Default { get; set; }
}

public class VaultDbCredentialsProvider(
    IServiceProvider provider,
    ILogger<VaultDbCredentialsProvider> logger,
    IOptions<DatabaseOptions> options) : BackgroundService, IVaultDbCredentialsProvider
{
    private DbCredentials? _cached;
    private DateTime       _expiresAt;

    public async Task<DbCredentials> GetCredentialsAsync()
    {
        if (_cached is not null && DateTime.UtcNow < _expiresAt.AddMinutes(-1))
            return _cached.Value;

        var vault = provider.GetRequiredService<IVaultClient>();

        var opt = options.Value;

        var secret = await vault.V1.Secrets.Database.GetCredentialsAsync(opt.RotationHolderRoleName, mountPoint: opt.RotationHolderSecretEngine);

        var username = secret.Data.Username;
        var password = secret.Data.Password;
        var ttl      = secret.LeaseDurationSeconds;

        _expiresAt = DateTime.UtcNow.AddSeconds(ttl);
        _cached    = new DbCredentials(username, password, ttl);

        logger.LogInformation("Vault credentials fetched: {User}, TTL={TTL}s", username, ttl);
        return _cached.Value;
    }

    public string BuildConnectionString()
    {
        var opt = options.Value;

        if (!opt.UseRotationHolder)
            return opt.ConnectionString;

        if (_cached is null)
            throw new IncorrectLoadingConfiguration("Argon is not loaded rotations of database secrets, cannot create ConnectionString...");
        return opt.ConnectionString.Replace("{username}", _cached.Value.username).Replace("{password}", _cached.Value.password);
    }

    public Task EnsureLoadedAsync()
    {
        var opt = options.Value;
        if (opt.UseRotationHolder)
            return GetCredentialsAsync();
        logger.LogWarning("Security warning, credentials rotation is not configured, going use default connection string for database.");
        return Task.CompletedTask;
    }

    public bool IsEnabled => options.Value.UseRotationHolder;

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opt = options.Value;
                if (!opt.UseRotationHolder)
                {
                    await Task.Delay(TimeSpan.FromSeconds(300), CancellationToken.None);
                    continue;
                }

                var credentials = await GetCredentialsAsync();
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(60, credentials.ttl - 60)), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch Vault DB credentials");
                throw;
            }
        }
    }
}

public class IncorrectLoadingConfiguration(string msg) : Exception(msg);