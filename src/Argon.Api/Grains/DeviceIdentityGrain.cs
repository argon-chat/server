namespace Argon.Grains;

using Argon.Entities;
using Argon.Features.Auth;
using Argon.Grains.Interfaces;
using Orleans.Concurrency;

/// <inheritdoc cref="IDeviceIdentityGrain"/>
[StatelessWorker]
public sealed class DeviceIdentityGrain(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    ILogger<DeviceIdentityGrain>            logger) : Grain, IDeviceIdentityGrain
{
    // A refresh reads; the sighting (LastProvenAt, LastSeenAt, Logins) is written at most this often
    // per account and machine.
    private static readonly TimeSpan SightingInterval = TimeSpan.FromHours(1);

    /// <remarks>
    /// There is no enrolment step: the first request carrying a valid proof is the enrolment. A barred
    /// machine still has its sighting recorded, since a banned machine still trying is exactly what a
    /// ban wants to know about, and then reads as no device so the caller refuses the bound token.
    /// </remarks>
    public async Task<Guid?> ResolveByKeyAsync(Guid userId, string publicKey, CancellationToken ct = default)
    {
        var thumbprint = DeviceProofVerifier.Thumbprint(publicKey);
        var now        = DateTimeOffset.UtcNow;
        var fresh      = now - SightingInterval;

        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);

            var seen = await db.DeviceKeys
               .AsNoTracking()
               .Where(k => k.Thumbprint == thumbprint)
               .Select(k => new
                {
                    k.DeviceId,
                    k.LastProvenAt,
                    ObservedAt = db.DeviceObservations
                       .Where(o => o.UserId == userId && o.DeviceId == k.DeviceId)
                       .Select(o => (DateTimeOffset?)o.LastSeenAt)
                       .FirstOrDefault(),
                    Banned = db.DeviceBans.Any(b => b.DeviceId == k.DeviceId && (b.ExpiresAt == null || b.ExpiresAt > now))
                })
               .FirstOrDefaultAsync(ct);

            if (seen is not { LastProvenAt: { } proven, ObservedAt: { } observed } || proven <= fresh || observed <= fresh)
            {
                var recorded = await db.Database.CreateExecutionStrategy().ExecuteAsync(
                    token => RecordSightingAsync(userId, thumbprint, publicKey, now, token), ct);

                return seen?.Banned == true ? null : recorded;
            }

            return seen.Banned ? null : seen.DeviceId;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not resolve a device key for {UserId}", userId);
            return null;
        }
    }

    public async Task<bool> IsBannedAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;

        return await db.DeviceBans
           .AsNoTracking()
           .AnyAsync(b => b.DeviceId == deviceId && (b.ExpiresAt == null || b.ExpiresAt > now), ct);
    }

    private async Task<Guid> RecordSightingAsync(
        Guid userId, string thumbprint, string publicKey, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var key = await db.DeviceKeys.FirstOrDefaultAsync(x => x.Thumbprint == thumbprint, ct);

        if (key is null)
        {
            key = new DeviceKeyEntity
            {
                Id           = ArgonId.New(),
                DeviceId     = ArgonId.New(),
                Thumbprint   = thumbprint,
                PublicKey    = publicKey,
                Platform     = DevicePlatform.UNKNOWN,
                Assurance    = DeviceAssurance.KEY,
                ClientName   = string.Empty,
                EnrolledAt   = now,
                LastProvenAt = now
            };

            db.DeviceKeys.Add(key);
        }
        else
            key.LastProvenAt = now;

        // Soft delete leaves a forgotten pair holding the unique (UserId, DeviceId) index, so it has to
        // be revived rather than inserted alongside — the filtered query cannot see it.
        var observation = await db.DeviceObservations
           .IgnoreQueryFilters()
           .FirstOrDefaultAsync(x => x.UserId == userId && x.DeviceId == key.DeviceId, ct);

        if (observation is null)
            db.DeviceObservations.Add(new DeviceObservationEntity
            {
                Id          = ArgonId.New(),
                UserId      = userId,
                DeviceId    = key.DeviceId,
                Components  = string.Empty,
                FirstSeenAt = now,
                LastSeenAt  = now,
                Logins      = 1
            });
        else
        {
            observation.IsDeleted  = false;
            observation.DeletedAt  = null;
            observation.LastSeenAt = now;
            observation.Logins++;
        }

        await db.SaveChangesAsync(ct);

        return key.DeviceId;
    }
}
