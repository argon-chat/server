namespace ArgonSharedLogicTest;

using Argon.Entities;
using Argon.Features.Cosmetics;

/// <summary>
/// What "already owned" means for an ownership row.
/// </summary>
/// <remarks>
/// An expired row is not treated as a duplicate: an item taken for 30 days would otherwise
/// permanently block its own way back, and seasonal keys exist for exactly this reason.
/// </remarks>
[TestFixture]
public class CosmeticGrantDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void No_row_grants()
        => Assert.That(CosmeticGrantDecision.For(null, Now), Is.EqualTo(CosmeticGrantOutcome.Granted));

    [Test]
    public void An_active_row_is_already_owned()
    {
        var existing = new CosmeticOwnershipEntity { UserId = Guid.NewGuid(), CosmeticItemId = Guid.NewGuid() };

        Assert.That(CosmeticGrantDecision.For(existing, Now), Is.EqualTo(CosmeticGrantOutcome.AlreadyOwned));
    }

    [Test]
    public void A_row_expiring_later_is_already_owned()
    {
        var existing = new CosmeticOwnershipEntity
        {
            UserId = Guid.NewGuid(), CosmeticItemId = Guid.NewGuid(), ExpiresAt = Now.AddDays(1)
        };

        Assert.That(CosmeticGrantDecision.For(existing, Now), Is.EqualTo(CosmeticGrantOutcome.AlreadyOwned));
    }

    [Test]
    public void An_expired_row_is_extended()
    {
        var existing = new CosmeticOwnershipEntity
        {
            UserId = Guid.NewGuid(), CosmeticItemId = Guid.NewGuid(), ExpiresAt = Now.AddDays(-1)
        };

        Assert.That(CosmeticGrantDecision.For(existing, Now), Is.EqualTo(CosmeticGrantOutcome.Extended));
    }

    /// <summary>
    /// At the exact instant a row expires, ownership has already ended.
    /// </summary>
    /// <remarks>
    /// The one value that pins the boundary between still-active and expired: without it the rule
    /// rests on nothing, since the other tests sit on either side of the line and would pass
    /// whichever way the boundary was drawn.
    /// </remarks>
    [Test]
    public void A_row_expiring_exactly_now_is_extended()
    {
        var existing = new CosmeticOwnershipEntity
        {
            UserId = Guid.NewGuid(), CosmeticItemId = Guid.NewGuid(), ExpiresAt = Now
        };

        Assert.That(CosmeticGrantDecision.For(existing, Now), Is.EqualTo(CosmeticGrantOutcome.Extended));
    }

    [Test]
    public void A_revoked_row_grants()
    {
        var existing = new CosmeticOwnershipEntity
        {
            UserId = Guid.NewGuid(), CosmeticItemId = Guid.NewGuid(), RevokedAt = Now.AddDays(-1)
        };

        Assert.That(CosmeticGrantDecision.For(existing, Now), Is.EqualTo(CosmeticGrantOutcome.Granted));
    }
}
