namespace Argon.Features.Integrations.Connections.Spotify;

using Argon.Services;

/// <summary>
/// Who is listening along with whom: one key per listener naming the host.
/// </summary>
/// <remarks>
/// The party itself lives in the host's <c>IListenAlongGrain</c>; this is the pointer the listener's
/// own calls need — <c>LeaveListenAlong</c> and <c>GetListenAlongState</c> arrive with the caller's
/// id and nothing else. Written by the host grain, read by the Ion service on the entry point, and
/// kept with a lifetime the host grain renews on every change, so a party that died with its silo
/// does not leave a pointer behind for long.
/// </remarks>
public sealed class ListenAlongDirectory(IArgonCacheDatabase cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    private static string Key(Guid listenerId) => $"conn:listenalong:{listenerId:N}";

    public Task SetHostAsync(Guid listenerId, Guid hostId, CancellationToken ct = default)
        => cache.StringSetAsync(Key(listenerId), hostId.ToString("N"), Lifetime, ct);

    public async Task<Guid?> GetHostAsync(Guid listenerId, CancellationToken ct = default)
        => Guid.TryParse(await cache.StringGetAsync(Key(listenerId), ct), out var host) ? host : null;

    public Task ClearAsync(Guid listenerId, CancellationToken ct = default)
        => cache.KeyDeleteAsync(Key(listenerId), ct);
}
