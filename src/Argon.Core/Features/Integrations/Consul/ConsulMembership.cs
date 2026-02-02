namespace Argon.Api.Features.Orleans.Consul;

using Argon.Features.Env;
using global::Consul;
using global::Consul.Filtering;
using global::Orleans.Configuration;
using System.Linq;
using System.Text.Json;
using VaultSharp.V1.SecretsEngines.Identity;

public class ConsulMembershipOptions
{
    public TimeSpan TTL            { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan DestroyTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetryAttempts { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    public List<string>? ExtendedTags { get; set; }
}

public class ConsulMembership(
    IConsulClient client,
    ILogger<IMembershipTable> logger,
    IOptions<ClusterOptions> clusterOptions,
    IOptions<ConsulMembershipOptions> membershipOptions,
    IHostApplicationLifetime lifetime,
    IHostEnvironment hostEnvironment,
    ILocalSiloDetails localSiloDetails,
    IServiceProvider provider) : IArgonUnitMembership, IDisposable, ISiloStatusListener
{
    private readonly CancellationTokenSource _shutdownCts = new();

    private ISiloStatusOracle? siloStatusOracle => provider.GetService<ISiloStatusOracle>();

    private async Task<TableVersion> GetTableVersion()
    {
        try
        {
            var ee = await client.KV.Get(ConsulOrleansTableVersion.Path);

            if (ee.StatusCode == HttpStatusCode.OK)
            {
                var json = Encoding.UTF8.GetString(ee.Response.Value);
                var consulVersion = JsonSerializer.Deserialize(json, ConsulJsonContext.Default.ConsulOrleansTableVersion)!;
                return consulVersion.ToTable();
            }

            var table = new TableVersion(0, Guid.NewGuid().ToString());
            var serialized = JsonSerializer.Serialize(ConsulOrleansTableVersion.Create(table), ConsulJsonContext.Default.ConsulOrleansTableVersion);
            await client.KV.Put(new KVPair(ConsulOrleansTableVersion.Path)
            {
                Value = Encoding.UTF8.GetBytes(serialized)
            });
            return table;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get table version from Consul");
            throw;
        }
    }

    private async Task UpdateTableVersion(TableVersion version)
        => await RetryAsync(async () =>
        {
            var serialized = JsonSerializer.Serialize(ConsulOrleansTableVersion.Create(version), ConsulJsonContext.Default.ConsulOrleansTableVersion);
            var result = await client.KV.Put(new KVPair(ConsulOrleansTableVersion.Path)
            {
                Value = Encoding.UTF8.GetBytes(serialized)
            });
            
            if (!result.Response)
                throw new InvalidOperationException("Failed to update table version in Consul KV store");
        }, "UpdateTableVersion");

    public Task InitializeMembershipTable(bool tryInitTableVersion) // not supported
        => Task.CompletedTask;

    public Task DeleteMembershipTableEntries(string clusterId) // not supported
        => Task.CompletedTask;

    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) // not supported
        => Task.CompletedTask;

    public async Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        try
        {
            var idSelector = new StringFieldSelector("ID");
            var services = await client.Agent.Services(idSelector == key.ToString());

            if (services.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("Failed to read silo {SiloAddress} from Consul: {StatusCode}", key, services.StatusCode);
                throw new InvalidOperationException($"Selector ServiceID == '{key}' returned '{services.StatusCode}'");
            }

            return services.Response.Count switch
            {
                0 => throw new InvalidOperationException($"Selector ServiceID == '{key}' returned empty list"),
                > 1 => throw new InvalidOperationException($"Selector ServiceID == '{key}' returned multiple services"),
                _ => new MembershipTableData(
                    new Tuple<MembershipEntry, string>(EjectEntry(services.Response.First().Value), ""),
                    await GetTableVersion())
            };
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            logger.LogError(ex, "Error reading row for silo {SiloAddress}", key);
            throw;
        }
    }

    public async Task<MembershipTableData> ReadAll()
    {
        try
        {
            var services = await client.Health.Service(IArgonUnitMembership.ArgonServiceName, IArgonUnitMembership.ArgonNameSpace, true);
            var table = await GetTableVersion();
            var list = services.Response
                .Select(x => new Tuple<MembershipEntry, string>(EjectEntry(x.Service), table.VersionEtag))
                .ToList();

            logger.LogDebug("Read {Count} silos from Consul", list.Count);
            return new MembershipTableData(list, table);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read all membership entries from Consul");
            throw;
        }
    }


    public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        try
        {
            var extendedTags = membershipOptions.Value.ExtendedTags ?? [];

            var service = new AgentServiceRegistration()
            {
                Name = IArgonUnitMembership.ArgonServiceName,
                Address = entry.SiloAddress.Endpoint.Address.ToString(),
                Port = entry.ProxyPort,
                ID = entry.SiloAddress.ToString(),
                Meta = GenerateMeta(entry),
                Checks =
                [
                    new AgentServiceCheck
                    {
                        CheckID = $"{IArgonUnitMembership.LoopBackHealth}.{entry.SiloAddress}",
                        TTL = membershipOptions.Value.TTL,
                        DeregisterCriticalServiceAfter = membershipOptions.Value.DestroyTimeout
                    }
                ],
                Tags = extendedTags.ToArray().Concat(
                [
                    IArgonUnitMembership.ArgonNameSpace,
                    hostEnvironment.IsWorker() ? IArgonUnitMembership.WorkerUnit :
                    hostEnvironment.IsGateway() ? IArgonUnitMembership.GatewayUnit : IArgonUnitMembership.EntryUnit,
                    Environment.MachineName
                ]).ToArray()
            };

            var sr = await client.Agent.ServiceRegister(service);
            sr.Assert();

            var currentTable = await GetTableVersion();
            await UpdateTableVersion(new TableVersion(tableVersion.Version + 1, currentTable.VersionEtag));
            await UpdateIAmAlive(entry);

            logger.LogInformation("Registered silo {SiloAddress} in Consul", entry.SiloAddress);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to insert row for silo {SiloAddress}", entry.SiloAddress);
            throw;
        }
    }

    public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        => InsertRow(entry, tableVersion);


    public async Task UpdateIAmAlive(MembershipEntry entry)
    {
        try
        {
            await client.Agent.UpdateTTL(
                $"{IArgonUnitMembership.LoopBackHealth}.{entry.SiloAddress}",
                $"Unit answered correctly! Currently status {siloStatusOracle?.CurrentStatus.ToString() ?? "Ok"}",
                ToStatus(entry));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update TTL for silo {SiloAddress}", entry.SiloAddress);
        }
    }

    public Task RegisterOnShutdownRules()
    {
        lifetime.ApplicationStopping.Register(async (_) =>
        {
            try
            {
                logger.LogInformation("Deregistering silo {SiloAddress} from Consul", localSiloDetails.SiloAddress);
                await client.Agent.ServiceDeregister(localSiloDetails.SiloAddress.ToString());
                logger.LogInformation("Successfully deregistered silo {SiloAddress}", localSiloDetails.SiloAddress);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deregister silo {SiloAddress} from Consul", localSiloDetails.SiloAddress);
            }
        }, null);

        siloStatusOracle?.SubscribeToSiloStatusEvents(this);

        return Task.CompletedTask;
    }

    public async void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
    {
        try
        {
            await client.Agent.UpdateTTL(
                $"{IArgonUnitMembership.LoopBackHealth}.{updatedSilo}",
                $"Unit answered correctly! Currently status {siloStatusOracle?.CurrentStatus.ToString() ?? "Ok"}",
                ToStatus(status));
            logger.LogInformation("Silo status changed {UpdatedSilo} -> {SiloStatus}", updatedSilo, status);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update TTL for silo {SiloAddress}", updatedSilo);
        }
    }

    private TTLStatus ToStatus(MembershipEntry entry)
        => ToStatus(entry.Status);

    private TTLStatus ToStatus(SiloStatus status)
    {
        if (siloStatusOracle?.CurrentStatus is null)
            return status switch
            {
                SiloStatus.None => TTLStatus.Pass,
                SiloStatus.Created or SiloStatus.Joining => TTLStatus.Pass,
                SiloStatus.Active => TTLStatus.Pass,
                SiloStatus.ShuttingDown or SiloStatus.Stopping => TTLStatus.Warn,
                SiloStatus.Dead => TTLStatus.Critical,
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown silo status")
            };
        return siloStatusOracle.CurrentStatus switch
        {
            SiloStatus.None         => TTLStatus.Critical,
            SiloStatus.Created      => TTLStatus.Warn,
            SiloStatus.Joining      => TTLStatus.Pass,
            SiloStatus.Active       => TTLStatus.Pass,
            SiloStatus.ShuttingDown => TTLStatus.Critical,
            SiloStatus.Stopping     => TTLStatus.Critical,
            SiloStatus.Dead         => TTLStatus.Critical,
            _                       => throw new ArgumentOutOfRangeException()
        };
    }

    private MembershipEntry EjectEntry(AgentService service)
    {
        if (service.Meta.TryGetValue("json", out var json))
        {
            try
            {
                var entry = JsonSerializer.Deserialize(json, ConsulJsonContext.Default.MembershipEntry);
                if (entry == null)
                    throw new InvalidOperationException("Deserialized MembershipEntry is null");

                entry.IAmAliveTime = DateTime.UtcNow.AddSeconds(-10);
                return entry;
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Failed to deserialize MembershipEntry from Consul service {ServiceId}", service.ID);
                throw new InvalidOperationException($"Failed to deserialize MembershipEntry from service {service.ID}", ex);
            }
        }

        throw new InvalidOperationException($"AgentService '{service.ID}' does not contain 'json' metadata");
    }

    private Dictionary<string, string> GenerateMeta(MembershipEntry entry)
    {
        try
        {
            var suspectTimesJson = entry.SuspectTimes is { Count: > 0 }
                ? JsonSerializer.Serialize(entry.SuspectTimes, ConsulJsonContext.Default.ListTupleSiloAddressDateTime)
                : "[]";

            return new Dictionary<string, string>
            {
                { "json", JsonSerializer.Serialize(entry, ConsulJsonContext.Default.MembershipEntry) },
                { "gen", entry.SiloAddress.Generation.ToString() },
                { "addr", JsonSerializer.Serialize(entry.SiloAddress, ConsulJsonContext.Default.SiloAddress) },
                { "proxy-port", entry.ProxyPort.ToString() },
                { "update-zone", entry.UpdateZone.ToString() },
                { "fault-zone", entry.FaultZone.ToString() },
                { "host-name", entry.HostName ?? string.Empty },
                { "silo-name", entry.SiloName ?? string.Empty },
                { "suspect-times", suspectTimesJson }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate metadata for silo {SiloAddress}", entry.SiloAddress);
            throw;
        }
    }

    private async Task RetryAsync(Func<Task> operation, string operationName)
    {
        var maxAttempts = membershipOptions.Value.MaxRetryAttempts;
        var delay = membershipOptions.Value.RetryDelay;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await operation();
                
                if (attempt > 1)
                    logger.LogInformation("{Operation} succeeded on attempt {Attempt}", operationName, attempt);
                
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var currentDelay = delay * Math.Pow(2, attempt - 1);
                logger.LogWarning(ex, "{Operation} failed on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}ms",
                    operationName, attempt, maxAttempts, currentDelay.TotalMilliseconds);
                
                await Task.Delay(currentDelay, _shutdownCts.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Operation} failed after {MaxAttempts} attempts", operationName, maxAttempts);
                throw;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _shutdownCts.Cancel();
        }
        catch { }
        try
        {
            _shutdownCts.Dispose();
        }
        catch  { }
    }
}