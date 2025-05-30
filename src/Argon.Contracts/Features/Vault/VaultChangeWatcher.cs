namespace Argon.Features.Vault;

/// <summary>
/// Background service to notify about Vault data changes.
/// </summary>
public class VaultChangeWatcher : BackgroundService
{
    private readonly ILogger?                                _logger;
    private readonly IEnumerable<VaultConfigurationProvider> _configProviders;

    /// <summary>
    /// Initializes a new instance of the <see cref="VaultChangeWatcher"/> class.
    /// test.
    /// </summary>
    /// <param name="configuration">Instance of IConfiguration</param>
    /// <param name="logger">Optional logger provider</param>
    public VaultChangeWatcher(IConfiguration configuration, ILogger? logger = null)
    {
        var configurationRoot = (IConfigurationRoot)configuration;
        if (configurationRoot == null)
            throw new ArgumentNullException(nameof(configurationRoot));

        this._logger = logger;
        this._configProviders = configurationRoot.Providers.OfType<VaultConfigurationProvider>().Where(p => p.ConfigurationSource.Options.ReloadOnChange).ToList()!;
    }

    /// <inheritdoc />
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timers = new Dictionary<int, int>(); // key - index of config provider, value - timer
        var minTime = int.MaxValue;
        var i = 0;
        foreach (var provider in this._configProviders)
        {
            var waitForSec = provider.ConfigurationSource.Options.ReloadCheckIntervalSeconds;
            minTime = Math.Min(minTime, waitForSec);
            timers[i] = waitForSec;
            i++;
        }

        this._logger?.LogInformation($"VaultChangeWatcher will use {minTime} seconds interval");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(minTime), stoppingToken).ConfigureAwait(false);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                for (var j = 0; j < this._configProviders.Count(); j++)
                {
                    var timer = timers[j];
                    timer -= minTime;
                    if (timer <= 0)
                    {
                        this._configProviders.ElementAt(j).Load();
                        timers[j] = this._configProviders.ElementAt(j).ConfigurationSource.Options
                            .ReloadCheckIntervalSeconds;
                    }
                    else
                    {
                        timers[j] = timer;
                    }
                }
            }
            catch (Exception ex)
            {
                this._logger?.LogError(ex, $"An exception occurred in {nameof(VaultChangeWatcher)}");
            }
        }
    }
}