namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Core.Features.CoreLogic.Privacy;
using Argon.Grains.Interfaces;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The "about me" privacy rules as they are evaluated: who a rule admits, what beats what, space
/// scoping, and how quickly a change takes effect.
/// </summary>
/// <remarks>
/// Rules are written the way the client writes them, through <c>PrivacyInteraction</c>; they are read
/// the way the product reads them, by asking the owner's <see cref="IPrivacyPolicyGrain"/> about a
/// viewer — which is what <c>ChannelGrain</c> does before letting someone draw on a stream.
/// </remarks>
[TestFixture]
public class PrivacyPolicyTests : TestBase
{
    private const string Key = PrivacyKeys.StreamDraw;

    private static IPrivacyPolicyGrain Policy(Guid ownerId) => SocialHarness.Grains.GetGrain<IPrivacyPolicyGrain>(ownerId);

    private static Task<bool> MayAsync(Guid ownerId, Guid viewerId, Guid? spaceId = null)
        => Policy(ownerId).EvaluateAsync(viewerId, Key, spaceId).AsTask();

    private static Task SetAsync(TestUserSession owner, PrivacyRuleMode mode, Guid? spaceId, CancellationToken ct,
        Guid[]? allow = null, Guid[]? deny = null)
        => owner.Privacy.SetPrivacyRule(Key, mode, spaceId, new IonArray<Guid>(allow ?? []), new IonArray<Guid>(deny ?? []), ct);

    [Test, CancelAfter(120_000)]
    public async Task The_owner_is_always_admitted_and_an_unset_key_admits_everybody(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var viewer = await CreateSessionAsync(ct);

        Assert.That(await MayAsync(owner.UserId, viewer.UserId), Is.True, "with no rule the key's default, everybody, applies");

        await SetAsync(owner, PrivacyRuleMode.NOBODY, null, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await MayAsync(owner.UserId, owner.UserId), Is.True, "nobody does not include the owner");
            Assert.That(await MayAsync(owner.UserId, viewer.UserId), Is.False);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Nobody_admits_only_the_allow_list_and_a_deny_beats_an_allow(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var allowed  = Guid.NewGuid();
        var both     = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        await SetAsync(owner, PrivacyRuleMode.NOBODY, null, ct, allow: [allowed, both], deny: [both]);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await MayAsync(owner.UserId, allowed), Is.True);
            Assert.That(await MayAsync(owner.UserId, both), Is.False, "deny has to beat allow");
            Assert.That(await MayAsync(owner.UserId, stranger), Is.False);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Everybody_with_a_deny_list_refuses_only_those_on_it(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var denied = Guid.NewGuid();

        await SetAsync(owner, PrivacyRuleMode.EVERYBODY, null, ct, deny: [denied]);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await MayAsync(owner.UserId, denied), Is.False);
            Assert.That(await MayAsync(owner.UserId, Guid.NewGuid()), Is.True);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Contacts_admits_friends_for_as_long_as_they_are_friends(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var friend   = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);

        await friend.Friends.SendFriendRequest(owner.Credentials.username, ct);
        await owner.Friends.AcceptFriendRequest(friend.UserId, ct);

        await SetAsync(owner, PrivacyRuleMode.CONTACTS, null, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await MayAsync(owner.UserId, friend.UserId), Is.True);
            Assert.That(await MayAsync(owner.UserId, stranger.UserId), Is.False);
        });

        await owner.Friends.RemoveFriend(friend.UserId, ct);

        Assert.That(await MayAsync(owner.UserId, friend.UserId), Is.False,
            "an unfriended viewer is still admitted — the friendship is not re-read");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_space_rule_overrides_the_global_one_only_inside_that_space(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var viewer = Guid.NewGuid();
        var space  = Guid.NewGuid();
        var other  = Guid.NewGuid();

        await SetAsync(owner, PrivacyRuleMode.NOBODY, null, ct);
        await SetAsync(owner, PrivacyRuleMode.EVERYBODY, space, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await MayAsync(owner.UserId, viewer, space), Is.True);
            Assert.That(await MayAsync(owner.UserId, viewer, other), Is.False, "another space falls back to the global rule");
            Assert.That(await MayAsync(owner.UserId, viewer), Is.False);

            var scoped = await owner.Privacy.GetPrivacyRule(Key, space, ct);
            Assert.That((scoped.mode, scoped.scopeSpaceId), Is.EqualTo((PrivacyRuleMode.EVERYBODY, (Guid?)space)));

            var global = await owner.Privacy.GetPrivacyRule(Key, other, ct);
            Assert.That((global.mode, global.scopeSpaceId), Is.EqualTo((PrivacyRuleMode.NOBODY, (Guid?)null)));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Changing_a_rule_updates_it_in_place_and_takes_effect_at_once(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var viewer = Guid.NewGuid();
        var denied = Guid.NewGuid();

        await SetAsync(owner, PrivacyRuleMode.NOBODY, null, ct);
        Assert.That(await MayAsync(owner.UserId, viewer), Is.False, "evaluated once, so the rules are now cached");

        await SetAsync(owner, PrivacyRuleMode.EVERYBODY, null, ct, deny: [denied]);

        var rule = await owner.Privacy.GetPrivacyRule(Key, null, ct);

        await using var db = await SocialHarness.DbAsync(ct);
        var rows = await db.Set<PrivacyRuleEntity>().CountAsync(r => r.UserId == owner.UserId && r.Key == Key, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await MayAsync(owner.UserId, viewer), Is.True, "the cached rule outlived the change");
            Assert.That(await MayAsync(owner.UserId, denied), Is.False);
            Assert.That(rows, Is.EqualTo(1), "a second write for the same key and scope must update, not add");
            Assert.That(rule.mode, Is.EqualTo(PrivacyRuleMode.EVERYBODY));
            Assert.That(rule.deny.Values, Is.EqualTo(new[] { denied }));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_key_the_server_does_not_know_is_refused_and_nothing_is_stored(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);

        Assert.That(async () => await Policy(owner.UserId).SetRuleAsync(
                new PrivacyRuleInput("not.a.key", PrivacyMode.Nobody, null, [], [])),
            Throws.InvalidOperationException);

        await using var db = await SocialHarness.DbAsync(ct);
        Assert.That(await db.Set<PrivacyRuleEntity>().CountAsync(r => r.UserId == owner.UserId, ct), Is.Zero);
    }
}
