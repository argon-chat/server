namespace Argon.Api.Features.Orleans.Consul;

using System.Text.Json;
using global::Consul;
using global::Orleans.GrainDirectory;
using global::Orleans.Runtime;
using Microsoft.Extensions.Options;

public class ConsulDirectoryOptions
{
    public string Directory { get; set; } = string.Empty;
    public int MaxRetryAttempts { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan SessionTTL { get; set; } = TimeSpan.FromSeconds(30);
}

public class ConsulDirectory(
    IConsulClient client,
    IOptions<ConsulDirectoryOptions> options,
    ILogger<IGrainDirectory> logger) : IGrainDirectory
{
    private const string ConsulPrefix = "orleans/grains/{0}/{1}";

    private readonly ConcurrentDictionary<SiloAddress, string> _sessions = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private async Task<string> EnsureSiloSessionAsync(SiloAddress address, bool useGlobalSearch = false)
    {
        if (_sessions.TryGetValue(address, out var existingSession))
        {
            logger.LogDebug("Using cached session {SessionId} for silo {SiloAddress}", existingSession, address);
            return existingSession;
        }

        await _sessionLock.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(address, out existingSession))
                return existingSession;

            if (useGlobalSearch)
            {
                var foundSession = await FindExistingSessionAsync(address);
                if (foundSession != null)
                {
                    _sessions.TryAdd(address, foundSession);
                    logger.LogInformation("Found existing session {SessionId} for silo {SiloAddress}", foundSession, address);
                    return foundSession;
                }
            }

            var newSession = await CreateSessionAsync(address);
            _sessions.TryAdd(address, newSession);
            logger.LogInformation("Created new session {SessionId} for silo {SiloAddress}", newSession, address);
            return newSession;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<string?> FindExistingSessionAsync(SiloAddress address)
    {
        try
        {
            var sessions = await client.Session.List();

            if (sessions.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("Failed to list Consul sessions: {StatusCode}", sessions.StatusCode);
                return null;
            }

            var siloName = address.ToString();
            var session = sessions.Response.FirstOrDefault(se => se.Name.Equals(siloName, StringComparison.Ordinal));
            
            return session?.ID;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error searching for existing session for silo {SiloAddress}", address);
            return null;
        }
    }

    private async Task<string> CreateSessionAsync(SiloAddress address)
    {
        return await RetryAsync(async () =>
        {
            var sessionEntry = new SessionEntry
            {
                Name = address.ToString(),
                Behavior = SessionBehavior.Delete,
                Checks = [$"{IArgonUnitMembership.LoopBackHealth}.{address}", "serfHealth"],
                LockDelay = TimeSpan.Zero,
                TTL = options.Value.SessionTTL
            };

            var response = await client.Session.Create(sessionEntry);

            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException($"Failed to create Consul session for silo {address}: {response.StatusCode}");

            return response.Response;
        }, $"CreateSession({address})");
    }

    public async Task<GrainAddress?> Register(GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(address.SiloAddress);

        try
        {
            var consulKey = ToPath(address.GrainId);
            var session = await EnsureSiloSessionAsync(address.SiloAddress, useGlobalSearch: true);

            await RetryAsync(async () =>
            {
                var json = JsonSerializer.Serialize(address, ConsulJsonContext.Default.GrainAddress);
                var kvPair = new KVPair(consulKey)
                {
                    Value = Encoding.UTF8.GetBytes(json),
                    Session = session
                };

                var result = await client.KV.Acquire(kvPair);
                
                if (!result.Response)
                {
                    logger.LogWarning("Failed to acquire lock for grain {GrainId} at {SiloAddress}", address.GrainId, address.SiloAddress);
                    throw new InvalidOperationException($"Failed to acquire Consul lock for grain {address.GrainId}");
                }
            }, $"Register({address.GrainId})");

            logger.LogDebug("Registered grain {GrainId} at silo {SiloAddress}", address.GrainId, address.SiloAddress);
            return address;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register grain {GrainId}", address.GrainId);
            throw;
        }
    }

    public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress)
    {
        if (previousAddress is null)
            return await Register(address);

        ArgumentNullException.ThrowIfNull(address.SiloAddress);
        ArgumentNullException.ThrowIfNull(previousAddress.SiloAddress);

        if (address.SiloAddress.Equals(previousAddress.SiloAddress))
        {
            logger.LogDebug("Grain {GrainId} staying on same silo {SiloAddress}", address.GrainId, address.SiloAddress);
            return await Register(address);
        }

        try
        {
            var prevKey = ToPath(previousAddress.GrainId);

            await RetryAsync(async () =>
            {
                var txn = await client.KV.Txn(
                [
                    new KVTxnOp(prevKey, KVTxnVerb.Unlock),
                    new KVTxnOp(prevKey, KVTxnVerb.Delete)
                ]);

                if (txn.StatusCode != HttpStatusCode.OK || !txn.Response.Success)
                {
                    var errors = txn.Response.Errors != null ? string.Join(", ", txn.Response.Errors) : "unknown";
                    throw new InvalidOperationException(
                        $"Failed to release previous grain registration for {previousAddress.GrainId} at {previousAddress.SiloAddress}. Errors: {errors}");
                }
            }, $"ReleasePrevious({previousAddress.GrainId})");

            logger.LogInformation("Released previous registration for grain {GrainId} from silo {PreviousSilo}, migrating to {NewSilo}",
                address.GrainId, previousAddress.SiloAddress, address.SiloAddress);

            return await Register(address);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to migrate grain {GrainId} from {PreviousSilo} to {NewSilo}",
                address.GrainId, previousAddress.SiloAddress, address.SiloAddress);
            throw;
        }
    }

    public async Task Unregister(GrainAddress address)
    {
        try
        {
            var consulKey = ToPath(address.GrainId);

            await RetryAsync(async () =>
            {
                var result = await client.KV.Delete(consulKey);
                
                if (!result.Response)
                    logger.LogWarning("Failed to delete key {ConsulKey} for grain {GrainId}", consulKey, address.GrainId);
            }, $"Unregister({address.GrainId})");

            logger.LogDebug("Unregistered grain {GrainId}", address.GrainId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to unregister grain {GrainId}", address.GrainId);
            throw;
        }
    }

    public async Task<GrainAddress?> Lookup(GrainId grainId)
    {
        try
        {
            var consulKey = ToPath(grainId);
            var result = await client.KV.Get(consulKey);

            if (result.StatusCode != HttpStatusCode.OK)
            {
                logger.LogDebug("Lookup failed for grain {GrainId}: {StatusCode}", grainId, result.StatusCode);
                return null;
            }

            if (result.Response?.Value == null)
            {
                logger.LogDebug("Grain {GrainId} not found in directory", grainId);
                return null;
            }

            var json = Encoding.UTF8.GetString(result.Response.Value);
            var address = JsonSerializer.Deserialize(json, ConsulJsonContext.Default.GrainAddress);

            if (address == null)
            {
                logger.LogWarning("Failed to deserialize grain address for {GrainId}", grainId);
                return null;
            }

            logger.LogDebug("Found grain {GrainId} at silo {SiloAddress}", grainId, address.SiloAddress);
            return address;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize grain address for {GrainId}", grainId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to lookup grain {GrainId}", grainId);
            throw;
        }
    }

    public async Task UnregisterSilos(List<SiloAddress> siloAddresses)
    {
        ArgumentNullException.ThrowIfNull(siloAddresses);

        if (siloAddresses.Count == 0)
        {
            logger.LogDebug("No silos to unregister");
            return;
        }

        try
        {
            var list = await client.Session.List();

            if (list.StatusCode != HttpStatusCode.OK)
            {
                logger.LogError("Failed to list sessions for silo cleanup: {StatusCode}", list.StatusCode);
                throw new InvalidOperationException($"Failed to list Consul sessions: {list.StatusCode}");
            }

            var siloNames = siloAddresses.Select(a => a.ToString()).ToHashSet(StringComparer.Ordinal);
            var sessionsToDestroy = list.Response.Where(s => siloNames.Contains(s.Name)).ToList();

            logger.LogInformation("Unregistering {Count} silo(s): {Silos}", siloAddresses.Count, string.Join(", ", siloAddresses));

            foreach (var session in sessionsToDestroy)
            {
                try
                {
                    await RetryAsync(async () =>
                    {
                        var result = await client.Session.Destroy(session.ID);
                        
                        if (result.StatusCode != HttpStatusCode.OK)
                            throw new InvalidOperationException($"Failed to destroy session {session.ID}: {result.StatusCode}");
                    }, $"DestroySession({session.Name})");

                    logger.LogInformation("Destroyed session {SessionId} for silo {SiloName}", session.ID, session.Name);

                    try
                    {
                        var addr = SiloAddress.FromParsableString(session.Name);
                        _sessions.TryRemove(addr, out _);
                    }
                    catch
                    {
                        // Ignore parse errors - session name might not be valid SiloAddress
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to destroy session {SessionId} for silo {SiloName}", session.ID, session.Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to unregister silos");
            throw;
        }
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> operation, string operationName)
    {
        var maxAttempts = options.Value.MaxRetryAttempts;
        var delay = options.Value.RetryDelay;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await operation();

                if (attempt > 1)
                    logger.LogInformation("{Operation} succeeded on attempt {Attempt}", operationName, attempt);

                return result;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var currentDelay = delay * Math.Pow(2, attempt - 1);
                logger.LogWarning(ex, "{Operation} failed on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}ms",
                    operationName, attempt, maxAttempts, currentDelay.TotalMilliseconds);

                await Task.Delay(currentDelay);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Operation} failed after {MaxAttempts} attempts", operationName, maxAttempts);
                throw;
            }
        }

        throw new InvalidOperationException($"{operationName} failed to complete");
    }

    private async Task RetryAsync(Func<Task> operation, string operationName)
    {
        var maxAttempts = options.Value.MaxRetryAttempts;
        var delay = options.Value.RetryDelay;

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

                await Task.Delay(currentDelay);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Operation} failed after {MaxAttempts} attempts", operationName, maxAttempts);
                throw;
            }
        }
    }

    private string ToPath(GrainId grainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Value.Directory, nameof(ConsulDirectoryOptions.Directory));
        return string.Format(ConsulPrefix, options.Value.Directory, grainId);
    }
}