namespace ArgonSharedLogicTest;

using Argon.Entities;
using Argon.Features.Cosmetics;

/// <summary>
/// The one rule about whether a catalogue row may be served, and that its two dialects agree.
/// </summary>
/// <remarks>
/// <para>The rule is written once with the moment as a parameter, and read two ways: compiled, for
/// a row already in hand, and rebound into a predicate a query can carry. The reason for the tests
/// is the rebinding — nothing about <c>ServableAt</c> being wrong would fail a build, and the
/// visible result of it drifting is a cosmetic that cannot be picked but is still worn.</para>
///
/// <para>The query dialect is exercised by compiling it rather than by running SQL, which is what
/// keeps this fixture free of a container. What it proves is that the two readings of one rule
/// return the same answer; that EF can translate the shape is the concern of the integration
/// tests.</para>
/// </remarks>
[TestFixture]
public class CosmeticAvailabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static CosmeticItemEntity Item(
        bool published = true,
        bool enabled = true,
        DateTimeOffset? from = null,
        DateTimeOffset? until = null)
        => new()
        {
            KindKey        = "profile.frame",
            Slug           = "autumn",
            NameKey        = "cosmetic.autumn",
            IsPublished    = published,
            IsEnabled      = enabled,
            AvailableFrom  = from,
            AvailableUntil = until
        };

    /// <summary>Both dialects, one answer. Every case below is asserted through this.</summary>
    private static bool Servable(CosmeticItemEntity item)
    {
        var inHand  = CosmeticAvailability.IsServable(item, Now);
        var inQuery = CosmeticAvailability.ServableAt(Now).Compile()(item);

        Assert.That(inQuery, Is.EqualTo(inHand), "the query dialect disagrees with the compiled one");

        return inHand;
    }

    [Test]
    public void A_published_enabled_row_with_no_window_is_servable()
        => Assert.That(Servable(Item()), Is.True);

    [Test]
    public void An_unpublished_row_is_not()
        => Assert.That(Servable(Item(published: false)), Is.False);

    [Test]
    public void A_switched_off_row_is_not()
        => Assert.That(Servable(Item(enabled: false)), Is.False);

    [Test]
    public void A_row_whose_window_has_not_opened_is_not()
        => Assert.That(Servable(Item(from: Now.AddMinutes(1))), Is.False);

    [Test]
    public void A_row_whose_window_opened_already_is()
        => Assert.That(Servable(Item(from: Now.AddMinutes(-1))), Is.True);

    /// <summary>The window is open at its start and shut at its end, so a row is never served twice.</summary>
    [Test]
    public void The_window_is_closed_on_the_instant_it_ends()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Servable(Item(from: Now)), Is.True);
            Assert.That(Servable(Item(until: Now)), Is.False);
            Assert.That(Servable(Item(until: Now.AddTicks(1))), Is.True);
        });
    }

    [Test]
    public void A_row_inside_a_window_on_both_sides_is_servable()
        => Assert.That(Servable(Item(from: Now.AddDays(-1), until: Now.AddDays(1))), Is.True);

    /// <summary>
    /// The moment is bound as a captured local rather than a constant, so that EF parameterises it
    /// instead of writing every distinct instant into the SQL as a literal.
    /// </summary>
    [Test]
    public void The_moment_is_not_baked_into_the_predicate_as_a_constant()
        => Assert.That(CosmeticAvailability.ServableAt(Now).ToString(), Does.Not.Contain(Now.Year.ToString()));
}
