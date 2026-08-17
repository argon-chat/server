namespace Argon.Features.Auth;

using Argon.Entities;
using Microsoft.EntityFrameworkCore;

/// <param name="DeviceId">The machine this login was attributed to.</param>
/// <param name="IsNewToThisAccount">No previous login from this account matched this machine.</param>
/// <param name="IsBanned">The machine is barred; the caller decides what to do about it.</param>
/// <param name="LinkedAccounts">
/// How many distinct accounts have signed in from this machine, including this one. A signal, not a
/// verdict — see <see cref="DeviceIdentityService"/>.
/// </param>
public readonly record struct DeviceIdentity(
    Guid DeviceId,
    bool IsNewToThisAccount,
    bool IsBanned,
    int LinkedAccounts);

/// <summary>
/// Works out which machine a login came from, and records that it did.
/// </summary>
/// <remarks>
/// <para>Matching is by score rather than equality, so a machine keeps its identity across the
/// changes people actually make — a new disk, a reformat, a reinstall — while two different
/// machines that happen to share a board model stay apart. <see cref="DeviceFingerprint"/> holds the
/// weights and the reasoning behind them.</para>
///
/// <para><b>The account count is a signal, not a verdict.</b> A shared family computer, a library, a
/// workshop machine and a legitimate second account all look identical from here. Acting on it
/// automatically punishes the first three to catch the fourth, so it is returned for a human or a
/// risk score to weigh and never enforced in this class.</para>
/// </remarks>
public class DeviceIdentityService(
    IDbContextFactory<ApplicationDbContext> context,
    ILogger<DeviceIdentityService> logger)
{
    /// <summary>
    /// Attributes a login to a machine, creating the record if this is a new one.
    /// </summary>
    /// <remarks>
    /// An empty fingerprint is not attributed at all. A client that reported nothing must not be
    /// merged with every other client that reported nothing — that would build one enormous device
    /// shared by every old build in the wild, and then ban it.
    /// </remarks>
    public async Task<DeviceIdentity?> ObserveAsync(Guid userId, DeviceFingerprint fingerprint, CancellationToken ct = default)
    {
        if (fingerprint.IsEmpty)
            return null;

        try
        {
            await using var ctx = await context.CreateDbContextAsync(ct);

            var deviceId = await ResolveAsync(ctx, userId, fingerprint, ct);
            var now      = DateTimeOffset.UtcNow;

            var existing = await ctx.DeviceObservations
               .FirstOrDefaultAsync(x => x.UserId == userId && x.DeviceId == deviceId, ct);

            var isNew = existing is null;

            if (existing is null)
            {
                ctx.DeviceObservations.Add(new DeviceObservationEntity
                {
                    Id          = Guid.CreateVersion7(),
                    UserId      = userId,
                    DeviceId    = deviceId,
                    Components  = Serialize(fingerprint),
                    FirstSeenAt = now,
                    LastSeenAt  = now,
                    Logins      = 1
                });
            }
            else
            {
                // The freshest reading wins: hardware changes, and scoring the next login against a
                // vector from two years ago is how a machine slowly stops recognising itself.
                existing.Components = Serialize(fingerprint);
                existing.LastSeenAt = now;
                existing.Logins++;
            }

            await ctx.SaveChangesAsync(ct);

            var linked = await ctx.DeviceObservations.CountAsync(x => x.DeviceId == deviceId, ct);
            var banned = await IsBannedAsync(ctx, deviceId, ct);

            return new DeviceIdentity(deviceId, isNew, banned, linked);
        }
        catch (Exception e)
        {
            // Device attribution is an anti-abuse signal, not a credential check. Failing to record
            // it must never be a reason someone cannot sign in.
            logger.LogError(e, "Could not attribute a device for {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Finds the machine behind a verified key, recording it the first time it is seen.
    /// </summary>
    /// <remarks>
    /// <para>There is no enrolment step to call, because there is no device service to call it on:
    /// the first request carrying a valid proof <em>is</em> the enrolment. That is the whole point of
    /// the identity riding the cookie — native code pushes it, the auth path reads it, and the
    /// contract never learns hardware exists.</para>
    ///
    /// <para>Returns null when the machine is barred, so a ban reads the same as having no device:
    /// the caller refuses the bound token and the session ends. Recording the sighting first is
    /// deliberate — a banned machine still trying is exactly what a ban wants to know about.</para>
    /// </remarks>
    public async Task<Guid?> ResolveByKeyAsync(Guid userId, DeviceProof proof, CancellationToken ct = default)
    {
        var thumbprint = DeviceProofVerifier.Thumbprint(proof.PublicKey);
        var now        = DateTimeOffset.UtcNow;

        try
        {
            await using var ctx = await context.CreateDbContextAsync(ct);

            var key = await ctx.DeviceKeys.FirstOrDefaultAsync(x => x.Thumbprint == thumbprint, ct);

            if (key is null)
            {
                key = new DeviceKeyEntity
                {
                    Id           = Guid.CreateVersion7(),
                    DeviceId     = Guid.CreateVersion7(),
                    Thumbprint   = thumbprint,
                    PublicKey    = proof.PublicKey,
                    Platform     = DevicePlatform.UNKNOWN,
                    Assurance    = DeviceAssurance.KEY,
                    ClientName   = string.Empty,
                    EnrolledAt   = now,
                    LastProvenAt = now
                };

                ctx.DeviceKeys.Add(key);
            }
            else
                key.LastProvenAt = now;

            // Soft delete leaves a forgotten pair holding the unique (UserId, DeviceId) index, so it
            // has to be revived rather than inserted alongside — the filtered query cannot see it.
            var observation = await ctx.DeviceObservations
               .IgnoreQueryFilters()
               .FirstOrDefaultAsync(x => x.UserId == userId && x.DeviceId == key.DeviceId, ct);

            if (observation is null)
                ctx.DeviceObservations.Add(new DeviceObservationEntity
                {
                    Id          = Guid.CreateVersion7(),
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

            await ctx.SaveChangesAsync(ct);

            return await IsBannedAsync(ctx, key.DeviceId, ct) ? null : key.DeviceId;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not resolve a device key for {UserId}", userId);
            return null;
        }
    }

    /// <summary>Accounts that have signed in from this machine, most recent first.</summary>
    public async Task<IReadOnlyList<Guid>> LinkedAccountsAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        return await ctx.DeviceObservations
           .Where(x => x.DeviceId == deviceId)
           .OrderByDescending(x => x.LastSeenAt)
           .Select(x => x.UserId)
           .ToListAsync(ct);
    }

    /// <summary>
    /// Finds the machine this fingerprint belongs to, or mints one.
    /// </summary>
    /// <remarks>
    /// <para>The candidate set is every machine on record, not only this account's: attributing a
    /// second account's login to the machine it actually came from is the entire point, and scoping
    /// the search to the account would guarantee every account got its own private device.</para>
    ///
    /// <para>Best match wins rather than first match, because a machine that was seen before a
    /// hardware change and again after leaves two records that both score above the threshold, and
    /// the closer one is the right home for a third login.</para>
    /// </remarks>
    private static async Task<Guid> ResolveAsync(
        ApplicationDbContext ctx, Guid userId, DeviceFingerprint fingerprint, CancellationToken ct)
    {
        // Narrowed by a component the fingerprint actually reported, so this is an index probe
        // rather than a scan of every device ever seen.
        var candidates = await ctx.DeviceObservations
           .Where(x => fingerprint.Components.Values.Any(v => x.Components.Contains(v)))
           .Select(x => new { x.DeviceId, x.Components })
           .Take(64)
           .ToListAsync(ct);

        var best      = Guid.Empty;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            var score = fingerprint.ScoreAgainst(DeviceFingerprint.Parse(candidate.Components));

            if (score >= DeviceFingerprint.SameMachineThreshold && score > bestScore)
            {
                best      = candidate.DeviceId;
                bestScore = score;
            }
        }

        return best == Guid.Empty ? Guid.CreateVersion7() : best;
    }

    private static async Task<bool> IsBannedAsync(ApplicationDbContext ctx, Guid deviceId, CancellationToken ct)
    {
        var ban = await ctx.DeviceBans.FirstOrDefaultAsync(x => x.DeviceId == deviceId, ct);

        return ban is not null && (ban.ExpiresAt is null || ban.ExpiresAt > DateTimeOffset.UtcNow);
    }

    private static string Serialize(DeviceFingerprint fingerprint)
        => $"{DeviceFingerprint.CurrentVersion};" +
           string.Join(",", fingerprint.Components.OrderBy(c => c.Key).Select(c => $"{c.Key}:{c.Value}"));
}
