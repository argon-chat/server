namespace Argon.Features.Auth;

using Argon.Core.Features.Transport;
using Argon.Entities;
using Argon.Services;

// The database half of the signed-in sessions registry. Grains and the flush grain only; the Ion
// layer writes through ISessionRegistryTransit.
public sealed class SessionRegistryStore(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ISessionRegistryTransit transit,
    IArgonCacheDatabase cache,
    ISessionRevocationBroadcaster revocations,
    ILogger<SessionRegistryStore> logger)
{
    // Stored rows with the unflushed transit changes on top, minus anything already tombstoned.
    public async Task<IReadOnlyList<SessionRegistryRecord>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.UserSessions.Where(x => x.UserId == userId).ToListAsync(ct);

        var byCredential = rows.ToDictionary(x => x.CredentialSessionId, SessionRegistryRecord.From);

        foreach (var pending in await transit.PendingAsync(userId, ct))
        {
            if (pending.Deleted)
            {
                byCredential.Remove(pending.CredentialSessionId);
                continue;
            }

            if (byCredential.TryGetValue(pending.CredentialSessionId, out var stored))
            {
                if (pending.Bare)
                {
                    if (pending.PresenceSessionId is { } presence)
                        stored.PresenceSessionId = presence;
                    if (pending.LastSeenAt > stored.LastSeenAt)
                        stored.LastSeenAt = pending.LastSeenAt;
                }
                else
                    byCredential[pending.CredentialSessionId] = pending with { CreatedAt = stored.CreatedAt };
            }
            else if (!pending.Bare)
                byCredential[pending.CredentialSessionId] = pending;
        }

        foreach (var revoked in await cache.SetMembersAsync(SessionRevocation.RevokedKey(userId), ct))
        {
            if (Guid.TryParse(revoked, out var credentialSessionId))
                byCredential.Remove(credentialSessionId);
        }

        return byCredential.Values.ToList();
    }

    public async Task RemoveAsync(Guid userId, IReadOnlyCollection<Guid> credentialSessionIds, CancellationToken ct = default)
    {
        if (credentialSessionIds.Count == 0)
            return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        await db.UserSessions
           .Where(x => x.UserId == userId && credentialSessionIds.Contains(x.CredentialSessionId))
           .ExecuteDeleteAsync(ct);

        foreach (var credentialSessionId in credentialSessionIds)
            await transit.MarkDeletedAsync(userId, credentialSessionId, ct);
    }

    public async Task RemoveAllAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        await db.UserSessions.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        await transit.ForgetUserAsync(userId, ct);
    }

    // Writes a flush batch: deletions first, then tracked updates and inserts in one SaveChanges,
    // falling back to one row at a time so a single bad record cannot hold the batch.
    public async Task<int> UpsertAsync(IReadOnlyList<SessionRegistryRecord> records, CancellationToken ct = default)
    {
        var dead = records.Where(r => r.Deleted).ToList();
        var live = records.Where(r => !r.Deleted).ToList();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        foreach (var group in dead.GroupBy(r => r.UserId))
        {
            var ids = group.Select(r => r.CredentialSessionId).ToList();

            await db.UserSessions
               .Where(x => x.UserId == group.Key && ids.Contains(x.CredentialSessionId))
               .ExecuteDeleteAsync(ct);
        }

        if (live.Count == 0)
            return dead.Count;

        var userIds  = live.Select(r => r.UserId).Distinct().ToList();
        var existing = await db.UserSessions.Where(x => userIds.Contains(x.UserId)).ToListAsync(ct);
        var byKey    = existing.ToDictionary(x => new SessionRegistryKey(x.UserId, x.CredentialSessionId));

        foreach (var record in live)
        {
            if (byKey.TryGetValue(record.Key, out var row))
                record.ApplyTo(row);
            else if (!record.Bare)
                db.UserSessions.Add(record.ToEntity());
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return dead.Count + live.Count;
        }
        catch (DbUpdateException e)
        {
            logger.LogWarning(e, "Writing {Count} session rows in one batch failed; retrying one at a time", live.Count);
        }

        var written = dead.Count;

        foreach (var record in live)
        {
            try
            {
                await UpsertOneAsync(record, ct);
                written++;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Could not write session {CredentialSessionId} of {UserId}; the change is dropped",
                    record.CredentialSessionId, record.UserId);
            }
        }

        return written;
    }

    private async Task UpsertOneAsync(SessionRegistryRecord record, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var row = await db.UserSessions.FirstOrDefaultAsync(
            x => x.UserId == record.UserId && x.CredentialSessionId == record.CredentialSessionId, ct);

        if (row is not null)
            record.ApplyTo(row);
        else if (record.Bare)
            return;
        else
            db.UserSessions.Add(record.ToEntity());

        await db.SaveChangesAsync(ct);
    }

    // Signs out devices not heard from since the cutoff: tombstone first, so a refresh token that
    // outlives its row is still dead, then the rows.
    public async Task<int> SweepStaleAsync(DateTimeOffset cutoff, int batch, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var stale = await db.UserSessions
           .Where(x => x.LastSeenAt < cutoff)
           .OrderBy(x => x.LastSeenAt)
           .Take(batch)
           .Select(x => new { x.UserId, x.CredentialSessionId })
           .ToListAsync(ct);

        if (stale.Count == 0)
            return 0;

        foreach (var group in stale.GroupBy(x => x.UserId))
        {
            var ids        = group.Select(x => x.CredentialSessionId).ToList();
            var revokedKey = SessionRevocation.RevokedKey(group.Key);

            foreach (var id in ids)
                await cache.SetAddAsync(revokedKey, id.ToString(), ct);

            await cache.UpdateStringExpirationAsync(revokedKey, SessionRevocation.Window, ct);
            await revocations.PublishAsync(group.Key, null, ids, ct);

            await db.UserSessions
               .Where(x => x.UserId == group.Key && ids.Contains(x.CredentialSessionId))
               .ExecuteDeleteAsync(ct);

            foreach (var id in ids)
                await transit.ForgetAsync(new SessionRegistryKey(group.Key, id), ct);
        }

        return stale.Count;
    }
}
