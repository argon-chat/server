namespace Argon.Features.Cosmetics;

using Argon.Entities;

/// <summary>What to do about an existing ownership row, if there is one.</summary>
public enum CosmeticGrantOutcome
{
    /// <summary>No row, or it was revoked — write a new one.</summary>
    Granted,

    /// <summary>The row exists but has expired — move its date forward.</summary>
    Extended,

    /// <summary>The row is active: there is nothing to grant.</summary>
    AlreadyOwned
}

/// <summary>
/// "Already owned" means an active row, not any row.
/// </summary>
/// <remarks>
/// A revoked row is history, not ownership: a new grant writes a new row and leaves the revocation
/// in place, so it stays visible that something was taken away. An expired row is the same
/// ownership with its time up, and moving its date is honester than starting a second row about
/// the same thing.
/// </remarks>
public static class CosmeticGrantDecision
{
    public static CosmeticGrantOutcome For(CosmeticOwnershipEntity? existing, DateTimeOffset now)
    {
        if (existing is null || existing.RevokedAt is not null)
            return CosmeticGrantOutcome.Granted;

        return existing.IsActiveAt(now) ? CosmeticGrantOutcome.AlreadyOwned : CosmeticGrantOutcome.Extended;
    }
}
