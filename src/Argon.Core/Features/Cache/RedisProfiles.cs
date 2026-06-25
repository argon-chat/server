namespace Argon.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

/// <summary>
/// Logical Redis "purposes". Each maps to an independently configured connection
/// (its own connection string + database) under the <c>Redis</c> configuration section,
/// so that e.g. Orleans clustering, the L2 cache and the general cache can each live on a
/// different server/database instead of all sharing one <c>cache</c> connection string.
/// </summary>
public static class RedisProfiles
{
    /// General-purpose cache pool: <see cref="IArgonCacheDatabase"/>, read-state, mute, realtime replay buffer.
    public const string Cache = "Cache";

    /// L2 distributed cache backing the L1/L2 HybridCache.
    public const string HybridCache = "HybridCache";

    /// Orleans grain storage (<c>RedisStorage</c>).
    public const string OrleansStorage = "OrleansStorage";

    /// Orleans clustering, reminders and the client gateway connection.
    public const string Orleans = "Orleans";

    /// SignalR backplane.
    public const string Backplane = "Backplane";

    /// Profiles that are served by a pooled <see cref="RedisConnectionPool"/> (rather than an own multiplexer).
    public static readonly string[] Pooled = [Cache, HybridCache, OrleansStorage];
}

/// <summary>One configured Redis connection: a connection string plus the default database and pool sizing.</summary>
public sealed class RedisProfileOptions
{
    /// StackExchange.Redis connection string. Required — there is no shared fallback.
    public string? ConnectionString { get; set; }

    /// Default Redis logical database for this profile. Callers may still address other databases explicitly.
    public int Database { get; set; }

    /// Upper bound on pooled connections (only used by pooled profiles). <c>0</c> → built-in default.
    public uint MaxSize { get; set; }
}

/// <summary>
/// Reads the <c>Redis</c> configuration section into named <see cref="RedisProfileOptions"/> and
/// resolves them for both pooled consumers and the components that build their own multiplexer
/// (Orleans clustering/reminders, SignalR backplane).
/// </summary>
public sealed class RedisProfileRegistry
{
    public const string SectionName     = "Redis";
    private const uint   DefaultMaxSize = 16;

    private readonly IReadOnlyDictionary<string, RedisProfileOptions> profiles;

    public RedisProfileRegistry(IConfiguration configuration)
        : this(configuration.GetSection(SectionName).Get<Dictionary<string, RedisProfileOptions>>())
    { }

    public RedisProfileRegistry(IDictionary<string, RedisProfileOptions>? profiles)
        => this.profiles = new Dictionary<string, RedisProfileOptions>(
            profiles ?? new Dictionary<string, RedisProfileOptions>(),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of every profile present in configuration (pooled and own-multiplexer alike). Used to health-check each scope.</summary>
    public IReadOnlyCollection<string> Names => profiles.Keys.ToArray();

    /// <summary>Returns the configured profile, throwing if it is missing or has no connection string.</summary>
    public RedisProfileOptions Resolve(string name)
    {
        if (!profiles.TryGetValue(name, out var profile) || string.IsNullOrWhiteSpace(profile.ConnectionString))
            throw new InvalidOperationException(
                $"Redis profile '{name}' is not configured. Add '{SectionName}:{name}:ConnectionString' " +
                $"(and optionally '{SectionName}:{name}:Database') to configuration.");
        return profile;
    }

    /// <summary>Pool size for a profile, falling back to a sane default when unset.</summary>
    public uint MaxSizeOf(string name)
    {
        var size = Resolve(name).MaxSize;
        return size == 0 ? DefaultMaxSize : size;
    }

    /// <summary>
    /// Parsed <see cref="ConfigurationOptions"/> for components that open their own multiplexer.
    /// The profile's <see cref="RedisProfileOptions.Database"/> is baked in as the default database.
    /// </summary>
    public ConfigurationOptions BuildOptions(string name)
    {
        var profile = Resolve(name);
        var options = ConfigurationOptions.Parse(profile.ConnectionString!);
        options.DefaultDatabase = profile.Database;
        return options;
    }
}
