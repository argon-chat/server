namespace Argon.Core.Features.Transport;

using Microsoft.AspNetCore.SignalR;

/// <summary>
/// One live hub connection on this node, and every identity a sign-out could name it by.
/// </summary>
/// <param name="SessionId">
/// The presence sid the ticket carries — the row on the devices screen, and what
/// <c>SecurityGrain.EndSessionAsync</c> tombstones when the button is pressed on it.
/// </param>
/// <param name="CredentialSessionIds">
/// The server-minted credential ids the same ticket carries as <c>csid</c> claims. A device that
/// signed out and came back under a freshly generated <c>scid</c> is only reachable through these —
/// see <see cref="Argon.Features.Auth.SessionRevocation"/> for why the two id spaces exist.
/// </param>
/// <param name="TicketIssuedAt">
/// When the ticket was minted, for the floor a sign-out-everywhere writes. Null for a ticket with no
/// <c>iat</c>, which the floor deliberately reads as older than any watermark.
/// </param>
/// <param name="Context">
/// The live caller context, kept solely so <see cref="HubConnectionRegistry"/> can call
/// <c>Abort()</c> on it. It is valid for as long as the connection is, and the connection removes
/// itself from the registry on the way out.
/// </param>
public sealed record HubConnectionEntry(
    string              ConnectionId,
    Guid                UserId,
    Guid                SessionId,
    IReadOnlyList<Guid> CredentialSessionIds,
    DateTimeOffset?     TicketIssuedAt,
    HubCallerContext    Context)
{
    /// <summary>The presence sid first, then every credential id — what a revocation is matched against.</summary>
    public IReadOnlyList<Guid> Identities => [SessionId, .. CredentialSessionIds];
}

/// <summary>
/// The live hub connections this node is holding, indexed by the two ids a revocation can name.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> A revocation is a fact written in Redis, and every gate that reads it
/// is on a path the <em>client</em> initiates: the Ion interceptor, the hub's own per-call check, the
/// session grain. So a signed-out device that simply stops talking is never asked. It keeps its
/// socket, keeps every space group it joined on connect, and goes on receiving messages, typing,
/// presence and roster events until it happens to say something — for the shipped desktop client
/// fifteen seconds away, and for a client that has been deliberately silenced, never. The tombstone
/// was correct the whole time and nobody consulted it.</para>
///
/// <para>Closing that needs a handle on the socket itself, and SignalR gives one only to the node
/// holding it: <c>HubCallerContext.Abort()</c>. This is that handle, kept per node, written by
/// <c>AppHub.OnConnectedAsync</c> after a successful attach and cleared by
/// <c>OnDisconnectedAsync</c>. Nothing else may add to it — an entry for a connection that was never
/// attached would be a handle to something the session does not believe in.</para>
///
/// <para><b>Local by construction, and that is the whole design constraint.</b> A connection can only
/// be aborted where it lives, so this can never be more than one node's share of a user's devices.
/// What makes a sign-out reach the other nodes is the cluster-wide signal in
/// <see cref="SessionRevocationSubscriber"/>, and what makes it reach them when that signal is lost
/// is <see cref="HubConnectionSweeper"/>. All three end here.</para>
///
/// <para><b>One lock rather than concurrent dictionaries.</b> Two indexes and a connection table have
/// to move together, and the obvious lock-free spelling has a real race in it: an index bucket
/// emptied by the last detach must be dropped or it leaks one entry per session the node has ever
/// seen, and dropping it can lose a connection that attached in between. Attach and detach happen
/// once per connection — never on a message path — so a lock costs nothing worth measuring and is
/// the version that is obviously correct.</para>
/// </remarks>
public sealed class HubConnectionRegistry(ILogger<HubConnectionRegistry> logger)
{
    private readonly Lock gate = new();

    private readonly Dictionary<string, HubConnectionEntry>   connections  = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>>      bySession    = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>>      byCredential = new(StringComparer.Ordinal);

    /// <summary>The presence-sid index key: the same <c>"{userId}:{sid}"</c> the session grain is keyed on.</summary>
    public static string SessionKey(Guid userId, Guid sessionId) => $"{userId}:{sessionId}";

    /// <summary>How many connections this node is holding. Diagnostics and tests only.</summary>
    public int Count
    {
        get
        {
            lock (gate) return connections.Count;
        }
    }

    /// <summary>Records a connection that has attached to its session.</summary>
    /// <remarks>
    /// Idempotent on the connection id: SignalR never reuses one, so a second call for the same id
    /// can only be a re-registration of the same socket, and replacing the entry is right.
    /// </remarks>
    public void Attach(HubConnectionEntry entry)
    {
        lock (gate)
        {
            if (connections.TryGetValue(entry.ConnectionId, out var existing))
                Unindex(existing);

            connections[entry.ConnectionId] = entry;

            Index(bySession, SessionKey(entry.UserId, entry.SessionId), entry.ConnectionId);

            foreach (var credentialId in entry.CredentialSessionIds)
                Index(byCredential, SessionKey(entry.UserId, credentialId), entry.ConnectionId);
        }
    }

    /// <summary>Forgets a connection. A no-op for one that was never attached.</summary>
    public void Detach(string connectionId)
    {
        lock (gate)
        {
            if (!connections.Remove(connectionId, out var entry))
                return;

            Unindex(entry);
        }
    }

    /// <summary>Closes every local connection holding the given presence sid.</summary>
    /// <returns>How many were closed here — nothing about the rest of the cluster.</returns>
    public ValueTask<int> AbortSessionAsync(Guid userId, Guid sessionId)
        => new(Abort(bySession, SessionKey(userId, sessionId), "the session was signed out"));

    /// <summary>Closes every local connection whose ticket carries the given credential id.</summary>
    /// <remarks>
    /// The half that reaches a device which rotated its <c>scid</c>: the presence sid on its ticket
    /// is one nobody has ever tombstoned, and the credential id is the one the server minted.
    /// </remarks>
    /// <returns>How many were closed here.</returns>
    public ValueTask<int> AbortCredentialAsync(Guid userId, Guid credentialSessionId)
        => new(Abort(byCredential, SessionKey(userId, credentialSessionId), "the credential was signed out"));

    /// <summary>Closes the given connections, whatever decided they should go.</summary>
    /// <remarks>For <see cref="HubConnectionSweeper"/>, which selects by re-reading the store itself.</remarks>
    public int Abort(IReadOnlyCollection<HubConnectionEntry> entries, string why)
    {
        var closed = 0;

        foreach (var entry in entries)
        {
            if (AbortOne(entry, why))
                closed++;
        }

        return closed;
    }

    /// <summary>Every connection this node holds, as of now.</summary>
    /// <remarks>
    /// A copy, deliberately: the sweeper re-reads Redis for each of these and must not hold the lock
    /// across a round trip. An entry that disconnects while the sweep is deciding about it is
    /// harmless — aborting a connection that is already gone is a no-op.
    /// </remarks>
    public IReadOnlyList<HubConnectionEntry> Snapshot()
    {
        lock (gate) return connections.Values.ToArray();
    }

    private int Abort(Dictionary<string, HashSet<string>> index, string key, string why)
    {
        HubConnectionEntry[] doomed;

        // Collected under the lock and aborted outside it: Abort() runs the connection's own
        // disconnect path, which comes back here to Detach.
        lock (gate)
        {
            if (!index.TryGetValue(key, out var ids))
                return 0;

            doomed = ids.Select(id => connections.GetValueOrDefault(id))
               .Where(x => x is not null)
               .ToArray()!;
        }

        return Abort(doomed, why);
    }

    private bool AbortOne(HubConnectionEntry entry, string why)
    {
        // Before the abort rather than after: the disconnect callback is what would normally remove
        // this, and it runs on the connection's own loop at a time of SignalR's choosing. Removing it
        // here is what stops the next sweep — or a second signal — spending its budget on a socket
        // that is already on its way out.
        Detach(entry.ConnectionId);

        try
        {
            entry.Context.Abort();

            logger.LogInformation(
                "Closed hub connection {ConnectionId} of session {SessionId} for user {UserId}: {Why}",
                entry.ConnectionId, entry.SessionId, entry.UserId, why);

            return true;
        }
        catch (Exception e)
        {
            // A connection that is already dead throws here, and that is the outcome we wanted. It is
            // reported rather than raised because the caller is a NATS handler or a timer, neither of
            // which has anywhere useful to put a failure.
            logger.LogDebug(e, "Could not close hub connection {ConnectionId} of session {SessionId}",
                entry.ConnectionId, entry.SessionId);

            return false;
        }
    }

    private void Index(Dictionary<string, HashSet<string>> index, string key, string connectionId)
    {
        if (!index.TryGetValue(key, out var ids))
            index[key] = ids = new HashSet<string>(StringComparer.Ordinal);

        ids.Add(connectionId);
    }

    private void Unindex(HubConnectionEntry entry)
    {
        Deindex(bySession, SessionKey(entry.UserId, entry.SessionId), entry.ConnectionId);

        foreach (var credentialId in entry.CredentialSessionIds)
            Deindex(byCredential, SessionKey(entry.UserId, credentialId), entry.ConnectionId);
    }

    private static void Deindex(Dictionary<string, HashSet<string>> index, string key, string connectionId)
    {
        if (!index.TryGetValue(key, out var ids))
            return;

        ids.Remove(connectionId);

        // Dropped as soon as it empties. A node that stays up for weeks sees a new sid on every
        // client launch, and a bucket kept for each of them is a leak that grows with uptime.
        if (ids.Count == 0)
            index.Remove(key);
    }
}
