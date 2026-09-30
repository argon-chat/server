namespace Argon.Features.Auth;

using Argon.Entities;
using Argon.Features.Clustering;
using Argon.Features.Logic;
using Argon.Services;
using ArgonContracts;
using System.Text.Json;

public sealed class SessionRegistryOptions : IValidatableFeatureOptions
{
    public const string SectionName = "SessionRegistry";

    // How often dirty transit records are written to the database, and how many per pass.
    public TimeSpan FlushInterval   { get; set; } = TimeSpan.FromMinutes(1);
    public int      FlushBatch      { get; set; } = 500;
    public int      MaxFlushPerTick { get; set; } = 5000;

    // How long a transit record outlives its last touch. Must comfortably cover a flush.
    public TimeSpan HotTtl { get; set; } = TimeSpan.FromDays(3);

    // A signed-in device not heard from for this long is signed out by the sweep. Zero disables it.
    public TimeSpan StaleAfter    { get; set; } = TimeSpan.FromDays(180);
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
    public int      SweepBatch    { get; set; } = 1000;

    public void Validate(IFeatureConfigurationReport report)
    {
        report.Require(FlushInterval > TimeSpan.Zero, nameof(FlushInterval), "must be positive");
        report.Require(FlushBatch > 0, nameof(FlushBatch), "must be positive");
        report.Require(MaxFlushPerTick >= FlushBatch, nameof(MaxFlushPerTick), $"must be at least {nameof(FlushBatch)}");
        report.Require(HotTtl > FlushInterval * 2, nameof(HotTtl), "a change must outlive the flush that writes it");
        report.Require(StaleAfter == TimeSpan.Zero || StaleAfter >= TimeSpan.FromDays(1), nameof(StaleAfter),
            "zero to disable, otherwise at least a day");
        report.Require(SweepInterval > TimeSpan.Zero, nameof(SweepInterval), "must be positive");
        report.Require(SweepBatch > 0, nameof(SweepBatch), "must be positive");
    }
}

public readonly record struct SessionRegistryKey(Guid UserId, Guid CredentialSessionId)
{
    public string Member => $"{UserId}:{CredentialSessionId}";

    public static bool TryParse(string member, out SessionRegistryKey key)
    {
        key = default;

        var split = member.IndexOf(':');
        if (split <= 0)
            return false;

        if (!Guid.TryParse(member.AsSpan(0, split), out var user) || !Guid.TryParse(member.AsSpan(split + 1), out var credential))
            return false;

        key = new SessionRegistryKey(user, credential);
        return true;
    }
}

// The transit copy of a row, and the shape the devices screen reads. Version orders touches so a
// flush can tell whether the record moved under it.
public sealed record SessionRegistryRecord
{
    public Guid  UserId              { get; init; }
    public Guid  CredentialSessionId { get; init; }
    public Guid? PresenceSessionId   { get; set; }

    public string         MachineId  { get; set; } = "";
    public string         ClientName { get; set; } = "";
    public string         Region     { get; set; } = "";
    public string         City       { get; set; } = "";
    public string         Ip         { get; set; } = "";
    public string         AppId      { get; set; } = "";
    public string         AppName    { get; set; } = "";
    public string         AppVersion { get; set; } = "";
    public ClientPlatform Platform   { get; set; }
    public string         OsName     { get; set; } = "";
    public string         DeviceName { get; set; } = "";

    public DateTimeOffset CreatedAt  { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    public long Version { get; set; }

    // Only ever seen through presence (a disconnect), never described: must not create a row.
    public bool Bare { get; set; }

    public bool Deleted { get; set; }

    public SessionRegistryKey Key => new(UserId, CredentialSessionId);

    public static SessionRegistryRecord From(UserSessionEntity row) => new()
    {
        UserId              = row.UserId,
        CredentialSessionId = row.CredentialSessionId,
        PresenceSessionId   = row.PresenceSessionId,
        MachineId           = row.MachineId,
        ClientName          = row.ClientName,
        Region              = row.Region,
        City                = row.City,
        Ip                  = row.Ip,
        AppId               = row.AppId,
        AppName             = row.AppName,
        AppVersion          = row.AppVersion,
        Platform            = row.Platform,
        OsName              = row.OsName,
        DeviceName          = row.DeviceName,
        CreatedAt           = row.CreatedAt,
        LastSeenAt          = row.LastSeenAt,
    };

    public void ApplyTo(UserSessionEntity row)
    {
        if (PresenceSessionId is { } presence)
            row.PresenceSessionId = presence;

        row.MachineId  = Keep(row.MachineId, MachineId);
        row.ClientName = Keep(row.ClientName, ClientName);
        row.Region     = Keep(row.Region, Region);
        row.City       = Keep(row.City, City);
        row.Ip         = Keep(row.Ip, Ip);
        row.AppId      = Keep(row.AppId, AppId);
        row.AppName    = Keep(row.AppName, AppName);
        row.AppVersion = Keep(row.AppVersion, AppVersion);
        row.OsName     = Keep(row.OsName, OsName);
        row.DeviceName = Keep(row.DeviceName, DeviceName);

        if (Platform != ClientPlatform.UNKNOWN)
            row.Platform = Platform;

        if (LastSeenAt > row.LastSeenAt)
            row.LastSeenAt = LastSeenAt;
    }

    public UserSessionEntity ToEntity()
    {
        var row = new UserSessionEntity
        {
            UserId              = UserId,
            CredentialSessionId = CredentialSessionId,
            CreatedAt           = CreatedAt,
            LastSeenAt          = LastSeenAt,
        };

        ApplyTo(row);
        return row;
    }

    internal static string Keep(string current, string? incoming)
        => string.IsNullOrWhiteSpace(incoming) ? current : incoming;
}

public sealed record SessionTouch(
    string? MachineId,
    Guid? PresenceSessionId,
    UserSessionMeta? Description,
    DateTimeOffset? CreatedAt,
    DateTimeOffset SeenAt)
{
    public static SessionTouch Now(string? machineId, Guid? presenceSessionId, UserSessionMeta? description, DateTimeOffset? createdAt = null)
        => new(machineId, presenceSessionId, description, createdAt, DateTimeOffset.UtcNow);
}

public interface ISessionRegistryTransit
{
    Task TouchAsync(Guid userId, Guid credentialSessionId, SessionTouch touch, CancellationToken ct = default);

    // A presence sid went away: stamp every credential recorded against it as seen just now.
    Task TouchSeenAsync(Guid userId, Guid presenceSessionId, DateTimeOffset seenAt, CancellationToken ct = default);

    Task MarkDeletedAsync(Guid userId, Guid credentialSessionId, CancellationToken ct = default);

    Task<IReadOnlyList<SessionRegistryRecord>> PendingAsync(Guid userId, CancellationToken ct = default);

    Task ForgetUserAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<SessionRegistryKey>> DirtyAsync(int count, CancellationToken ct = default);

    Task<SessionRegistryRecord?> HotAsync(SessionRegistryKey key, CancellationToken ct = default);

    // Drops the dirty mark unless the record was touched again since it was read at that version.
    Task<bool> TryClearDirtyAsync(SessionRegistryKey key, long version, CancellationToken ct = default);

    Task ForgetAsync(SessionRegistryKey key, CancellationToken ct = default);
}

public sealed class SessionRegistryTransit(
    IArgonCacheDatabase cache,
    IOptions<SessionRegistryOptions> options,
    ILogger<SessionRegistryTransit> logger) : ISessionRegistryTransit
{
    private const string DirtyKey = "sessreg:dirty";

    private static string HotKey(Guid userId, Guid credentialSessionId) => $"sessreg:hot:{userId}:{credentialSessionId}";
    private static string PendingKey(Guid userId) => $"sessreg:pending:{userId}";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task TouchAsync(Guid userId, Guid credentialSessionId, SessionTouch touch, CancellationToken ct = default)
    {
        if (credentialSessionId == Guid.Empty)
            return;

        try
        {
            var existing = await ReadAsync(HotKey(userId, credentialSessionId), ct);

            // A deleted marker is only lifted by a real use of the credential, not by a disconnect.
            if (existing is { Deleted: true } && touch.Description is null && string.IsNullOrWhiteSpace(touch.MachineId))
                return;

            var record = existing is { Deleted: false }
                ? existing
                : new SessionRegistryRecord
                {
                    UserId              = userId,
                    CredentialSessionId = credentialSessionId,
                    CreatedAt           = touch.CreatedAt ?? touch.SeenAt,
                    LastSeenAt          = touch.SeenAt,
                    Bare                = true,
                };

            Apply(record, touch);
            await WriteAsync(record, existing, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not record session {CredentialSessionId} of {UserId} in the transit", credentialSessionId, userId);
        }
    }

    public async Task TouchSeenAsync(Guid userId, Guid presenceSessionId, DateTimeOffset seenAt, CancellationToken ct = default)
    {
        if (presenceSessionId == Guid.Empty || presenceSessionId == Guid.AllBitsSet)
            return;

        string[] credentials;

        try
        {
            credentials = await SessionRevocation.CredentialSessionsAsync(cache, userId, presenceSessionId, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read the credential sessions of {SessionId} for {UserId}", presenceSessionId, userId);
            return;
        }

        foreach (var credential in credentials)
        {
            if (Guid.TryParse(credential, out var credentialSessionId))
                await TouchAsync(userId, credentialSessionId, new SessionTouch(null, presenceSessionId, null, null, seenAt), ct);
        }
    }

    public async Task MarkDeletedAsync(Guid userId, Guid credentialSessionId, CancellationToken ct = default)
    {
        try
        {
            var existing = await ReadAsync(HotKey(userId, credentialSessionId), ct);

            var record = existing ?? new SessionRegistryRecord
            {
                UserId              = userId,
                CredentialSessionId = credentialSessionId,
                CreatedAt           = DateTimeOffset.UtcNow,
                LastSeenAt          = DateTimeOffset.UtcNow,
            };

            record.Deleted = true;
            await WriteAsync(record, existing, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not mark session {CredentialSessionId} of {UserId} deleted in the transit", credentialSessionId, userId);
        }
    }

    public async Task<IReadOnlyList<SessionRegistryRecord>> PendingAsync(Guid userId, CancellationToken ct = default)
    {
        var members = await cache.SetMembersAsync(PendingKey(userId), ct);

        if (members.Length == 0)
            return [];

        var list = new List<SessionRegistryRecord>(members.Length);

        foreach (var member in members)
        {
            if (!Guid.TryParse(member, out var credentialSessionId))
            {
                await cache.SetRemoveAsync(PendingKey(userId), member, ct);
                continue;
            }

            var record = await ReadAsync(HotKey(userId, credentialSessionId), ct);

            if (record is null)
            {
                await cache.SetRemoveAsync(PendingKey(userId), member, ct);
                continue;
            }

            list.Add(record);
        }

        return list;
    }

    public async Task ForgetUserAsync(Guid userId, CancellationToken ct = default)
    {
        foreach (var member in await cache.SetMembersAsync(PendingKey(userId), ct))
        {
            if (!Guid.TryParse(member, out var credentialSessionId))
                continue;

            await cache.KeyDeleteAsync(HotKey(userId, credentialSessionId), ct);
            await cache.SortedSetRemoveAsync(DirtyKey, new SessionRegistryKey(userId, credentialSessionId).Member, ct);
        }

        await cache.KeyDeleteAsync(PendingKey(userId), ct);
    }

    public async Task<IReadOnlyList<SessionRegistryKey>> DirtyAsync(int count, CancellationToken ct = default)
    {
        var members = await cache.SortedSetRangeAsync(DirtyKey, 0, count, descending: false, ct);
        var keys    = new List<SessionRegistryKey>(members.Length);

        foreach (var member in members)
        {
            if (SessionRegistryKey.TryParse(member, out var key))
                keys.Add(key);
            else
                await cache.SortedSetRemoveAsync(DirtyKey, member, ct);
        }

        return keys;
    }

    public Task<SessionRegistryRecord?> HotAsync(SessionRegistryKey key, CancellationToken ct = default)
        => ReadAsync(HotKey(key.UserId, key.CredentialSessionId), ct);

    public async Task<bool> TryClearDirtyAsync(SessionRegistryKey key, long version, CancellationToken ct = default)
    {
        var current = await ReadAsync(HotKey(key.UserId, key.CredentialSessionId), ct);

        if (current is not null && current.Version != version)
            return false;

        await cache.SortedSetRemoveAsync(DirtyKey, key.Member, ct);
        await cache.SetRemoveAsync(PendingKey(key.UserId), key.CredentialSessionId.ToString(), ct);
        return true;
    }

    public async Task ForgetAsync(SessionRegistryKey key, CancellationToken ct = default)
    {
        await cache.KeyDeleteAsync(HotKey(key.UserId, key.CredentialSessionId), ct);
        await cache.SetRemoveAsync(PendingKey(key.UserId), key.CredentialSessionId.ToString(), ct);
        await cache.SortedSetRemoveAsync(DirtyKey, key.Member, ct);
    }

    private static void Apply(SessionRegistryRecord record, SessionTouch touch)
    {
        if (touch.SeenAt > record.LastSeenAt)
            record.LastSeenAt = touch.SeenAt;

        if (touch.PresenceSessionId is { } presence && presence != Guid.Empty && presence != Guid.AllBitsSet)
            record.PresenceSessionId = presence;

        if (!string.IsNullOrWhiteSpace(touch.MachineId))
        {
            record.MachineId = Cap(touch.MachineId, 64);
            record.Bare      = false;
        }

        if (touch.Description is not { } d)
            return;

        record.Bare       = false;
        record.ClientName = Keep(record.ClientName, d.ClientName, 512);
        record.Region     = Keep(record.Region, d.Region, 8);
        record.City       = Keep(record.City, d.City, 128);
        record.Ip         = Keep(record.Ip, d.Ip, 64);
        record.AppId      = Keep(record.AppId, d.AppId, 64);
        record.AppName    = Keep(record.AppName, d.AppName, 128);
        record.AppVersion = Keep(record.AppVersion, d.AppVersion, 64);
        record.OsName     = Keep(record.OsName, d.OsName, 128);
        record.DeviceName = Keep(record.DeviceName, d.DeviceName, 128);

        if (d.Platform != ClientPlatform.UNKNOWN)
            record.Platform = d.Platform;
    }

    private static string Keep(string current, string? incoming, int max)
        => string.IsNullOrWhiteSpace(incoming) ? current : Cap(incoming, max);

    private static string Cap(string value, int max)
        => value.Length <= max ? value : value[..max];

    private async Task WriteAsync(SessionRegistryRecord record, SessionRegistryRecord? previous, CancellationToken ct)
    {
        var ttl = options.Value.HotTtl;

        record.Version = Math.Max((previous?.Version ?? 0) + 1, DateTimeOffset.UtcNow.UtcTicks);

        await cache.StringSetAsync(HotKey(record.UserId, record.CredentialSessionId), JsonSerializer.Serialize(record, Json), ttl, ct);
        await cache.SetAddAsync(PendingKey(record.UserId), record.CredentialSessionId.ToString(), ct);
        await cache.UpdateStringExpirationAsync(PendingKey(record.UserId), ttl, ct);
        await cache.SortedSetAddAsync(DirtyKey, record.Key.Member, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
    }

    private async Task<SessionRegistryRecord?> ReadAsync(string key, CancellationToken ct)
    {
        var json = await cache.StringGetAsync(key, ct);

        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SessionRegistryRecord>(json, Json);
        }
        catch (JsonException e)
        {
            logger.LogWarning(e, "Unreadable session registry record at {Key}; dropping it", key);
            await cache.KeyDeleteAsync(key, ct);
            return null;
        }
    }
}
