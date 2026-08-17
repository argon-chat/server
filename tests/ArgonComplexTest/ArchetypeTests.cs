namespace ArgonComplexTest.Tests;

using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Archetypes (roles) and per-channel entitlement overwrites — the write side of the permission
/// model whose read side <c>EntitlementEvaluatorTests</c> covers as pure logic. Nothing here had
/// coverage, which meant the code that decides who can be granted what ran only in production.
/// </summary>
[TestFixture]
public class ArchetypeTests : TestBase
{
    private IArchetypeInteraction Archetypes(IServiceProvider provider)
        => IonClient.ForService<IArchetypeInteraction>(provider);

    private async Task<Guid> NewSpaceAsync(CancellationToken ct)
    {
        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        return await CreateSpaceAndGetIdAsync(ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task GetServerArchetypes_ANewSpaceHasItsDefaults(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var archetypes = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(archetypes.Size, Is.GreaterThanOrEqualTo(2), "a space is seeded with 'everyone' and 'owner'");
            Assert.That(archetypes.Values.Select(a => a.name), Does.Contain("everyone"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task CreateArchetype_AppearsInTheSpace(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var created = await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "moderators", ct);

        Assert.Multiple(() =>
        {
            Assert.That(created.name, Is.EqualTo("moderators"));
            Assert.That(created.spaceId, Is.EqualTo(spaceId));
            Assert.That(created.isLocked, Is.False);
        });

        var all = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);
        Assert.That(all.Values.Select(a => a.id), Does.Contain(created.id));
    }

    [Test, CancelAfter(120_000)]
    public async Task UpdateArchetype_PersistsNameAndEntitlements(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var created = await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "before", ct);

        var updated = await Archetypes(scope.ServiceProvider).UpdateArchetype(
            spaceId,
            created with
            {
                name        = "after",
                description = "updated by tests",
                entitlement = ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages
            },
            ct);

        Assert.Multiple(() =>
        {
            Assert.That(updated.name, Is.EqualTo("after"));
            Assert.That(updated.entitlement.HasFlag(ArgonEntitlement.SendMessages), Is.True);
        });

        var reloaded = (await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct))
           .Values.First(a => a.id == created.id);

        Assert.That(reloaded.name, Is.EqualTo("after"));
    }

    [Test, CancelAfter(120_000)]
    public async Task GetDetailedServerArchetypes_IncludesMembership(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var groups = await Archetypes(scope.ServiceProvider).GetDetailedServerArchetypes(spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(groups.Size, Is.GreaterThanOrEqualTo(2));
            Assert.That(groups.Values.Select(g => g.archetype.name), Does.Contain("everyone"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task SetArchetypeToMember_GrantsThenRevokes(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var role = await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "granted-role", ct);

        // The space creator is its first member; find their membership id.
        var members = await GetServerService(scope.ServiceProvider).GetMembers(spaceId, ct);
        var memberId = members.Values.First().member.memberId;

        var granted = await Archetypes(scope.ServiceProvider).SetArchetypeToMember(spaceId, memberId, role.id, true, ct);
        Assert.That(granted, Is.True);

        var afterGrant = await Archetypes(scope.ServiceProvider).GetDetailedServerArchetypes(spaceId, ct);
        Assert.That(
            afterGrant.Values.First(g => g.archetype.id == role.id).members.Values,
            Does.Contain(memberId));

        var revoked = await Archetypes(scope.ServiceProvider).SetArchetypeToMember(spaceId, memberId, role.id, false, ct);
        Assert.That(revoked, Is.True);

        var afterRevoke = await Archetypes(scope.ServiceProvider).GetDetailedServerArchetypes(spaceId, ct);
        Assert.That(
            afterRevoke.Values.First(g => g.archetype.id == role.id).members.Values,
            Does.Not.Contain(memberId));
    }

    [Test, CancelAfter(120_000)]
    public async Task ChannelOverwrite_UpsertThenReadThenDelete(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"overwrites-{Guid.NewGuid():N}"[..20], ct);

        var role = await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "restricted", ct);

        var upserted = await Archetypes(scope.ServiceProvider).UpsertArchetypeEntitlementForChannel(
            spaceId, channelId, role.id,
            deny: ArgonEntitlement.SendMessages,
            allow: ArgonEntitlement.ViewChannel,
            ct);

        Assert.That(upserted, Is.Not.Null);

        var overwrites = await Archetypes(scope.ServiceProvider).GetChannelEntitlementOverwrites(spaceId, channelId, ct);
        var mine = overwrites.Values.FirstOrDefault(o => o.archetypeId == role.id);

        Assert.That(mine, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(mine!.deny.HasFlag(ArgonEntitlement.SendMessages), Is.True);
            Assert.That(mine.allow.HasFlag(ArgonEntitlement.ViewChannel), Is.True);
        });

        // Upserting again must update in place rather than accumulate duplicates — two overwrites
        // for the same archetype on the same channel would make evaluation order-dependent.
        await Archetypes(scope.ServiceProvider).UpsertArchetypeEntitlementForChannel(
            spaceId, channelId, role.id, deny: ArgonEntitlement.None, allow: ArgonEntitlement.SendMessages, ct);

        var afterSecondUpsert = await Archetypes(scope.ServiceProvider).GetChannelEntitlementOverwrites(spaceId, channelId, ct);
        Assert.That(afterSecondUpsert.Values.Count(o => o.archetypeId == role.id), Is.EqualTo(1));

        var deleted = await Archetypes(scope.ServiceProvider).DeleteEntitlementForChannel(spaceId, channelId, mine!.id, ct);
        Assert.That(deleted, Is.True);

        var afterDelete = await Archetypes(scope.ServiceProvider).GetChannelEntitlementOverwrites(spaceId, channelId, ct);
        Assert.That(afterDelete.Values.Select(o => o.id), Does.Not.Contain(mine.id));
    }

    [Test, CancelAfter(120_000)]
    public async Task GetChannelEntitlementOverwrites_ForAFreshChannel_IsEmpty(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"plain-{Guid.NewGuid():N}"[..20], ct);

        var overwrites = await Archetypes(scope.ServiceProvider).GetChannelEntitlementOverwrites(spaceId, channelId, ct);

        Assert.That(overwrites.Size, Is.EqualTo(0));
    }

    [Test, CancelAfter(120_000)]
    public async Task DeleteEntitlementForChannel_ForAnUnknownOverwrite_ReturnsFalse(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"nodel-{Guid.NewGuid():N}"[..20], ct);

        var deleted = await Archetypes(scope.ServiceProvider)
           .DeleteEntitlementForChannel(spaceId, channelId, Guid.NewGuid(), ct);

        Assert.That(deleted, Is.False);
    }

    // ── Deleting a role ──────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task DeleteArchetype_RemovesItFromTheSpace(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var archetype = await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "temps", ct);

        var result = await Archetypes(scope.ServiceProvider).DeleteArchetype(spaceId, archetype.id, ct);
        Assert.That(result, Is.InstanceOf<SuccessDeleteArchetype>());

        var all = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);
        Assert.That(all.Values.Select(a => a.id), Does.Not.Contain(archetype.id));
    }

    [Test, CancelAfter(120_000)]
    public async Task DeleteArchetype_TakesItsGrantsWithIt(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var archetype = await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "doomed", ct);

        var detailed = await Archetypes(scope.ServiceProvider).GetDetailedServerArchetypes(spaceId, ct);
        var me       = detailed.Values.SelectMany(g => g.members.Values).FirstOrDefault();

        if (me != Guid.Empty)
            await Archetypes(scope.ServiceProvider).SetArchetypeToMember(spaceId, me, archetype.id, true, ct);

        await Archetypes(scope.ServiceProvider).DeleteArchetype(spaceId, archetype.id, ct);

        // A grant outliving its role is a row pointing at nothing, and the member list would keep
        // grouping people under a role that is gone.
        var after = await Archetypes(scope.ServiceProvider).GetDetailedServerArchetypes(spaceId, ct);
        Assert.That(after.Values.Select(g => g.archetype.id), Does.Not.Contain(archetype.id));
    }

    [Test, CancelAfter(120_000)]
    public async Task DeleteArchetype_RefusesTheDefaults(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var all = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);

        Assert.Multiple(async () =>
        {
            foreach (var seeded in all.Values.Where(a => a.isDefault))
            {
                var result = await Archetypes(scope.ServiceProvider).DeleteArchetype(spaceId, seeded.id, ct);

                // These are what the permission system falls back to when a member holds nothing
                // else. Removing one leaves those members with no answer at all.
                Assert.That(result, Is.InstanceOf<FailedDeleteArchetype>(), $"'{seeded.name}' must not be deletable");
                Assert.That(((FailedDeleteArchetype)result).error, Is.EqualTo(ArchetypeError.IS_DEFAULT));
            }
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task DeleteArchetype_WithUnknownId_ReturnsNotFound(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var result = await Archetypes(scope.ServiceProvider).DeleteArchetype(spaceId, Guid.NewGuid(), ct);

        Assert.That(result, Is.InstanceOf<FailedDeleteArchetype>());
        Assert.That(((FailedDeleteArchetype)result).error, Is.EqualTo(ArchetypeError.NOT_FOUND));
    }

    [Test, CancelAfter(120_000)]
    public async Task DeleteArchetype_FromAStranger_IsRefused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var archetype = await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "notyours", ct);

        // Someone who is not in the space at all. Anything but a refusal here is a way to dismantle
        // a stranger's permission model with nothing but a space id.
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var result = await Archetypes(scope.ServiceProvider).DeleteArchetype(spaceId, archetype.id, ct);

        Assert.That(result, Is.InstanceOf<FailedDeleteArchetype>());
        Assert.That(((FailedDeleteArchetype)result).error, Is.EqualTo(ArchetypeError.NO_PERMISSION));
    }

    // ── Ranking ──────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task ReorderArchetypes_WritesTheRankItWasGiven(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);
        await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "mods", ct);

        var all     = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);
        var ordered = all.Values.Select(a => a.id).Reverse().ToArray();

        var result = await Archetypes(scope.ServiceProvider)
           .ReorderArchetypes(spaceId, new IonArray<Guid>(ordered), ct);

        Assert.That(result, Is.InstanceOf<SuccessReorderArchetypes>());

        var ranked = ((SuccessReorderArchetypes)result).archetypes.Values.ToList();

        Assert.Multiple(() =>
        {
            // Highest first, and dense: a gap or a repeat means two roles claim the same rank and
            // the member list has to break the tie by guessing again.
            Assert.That(ranked.Select(a => a.id), Is.EqualTo(ordered).AsCollection);
            Assert.That(ranked.Select(a => a.order), Is.EqualTo(Enumerable.Range(0, ordered.Length).Cast<int?>()).AsCollection);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task ReorderArchetypes_SurvivesAReadBack(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);
        await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "keepers", ct);

        var all     = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);
        var ordered = all.Values.Select(a => a.id).Reverse().ToArray();

        await Archetypes(scope.ServiceProvider).ReorderArchetypes(spaceId, new IonArray<Guid>(ordered), ct);

        // The returned list is easy to get right by accident; the next read is what clients see.
        var reread = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);

        Assert.That(
            reread.Values.OrderBy(a => a.order).Select(a => a.id),
            Is.EqualTo(ordered).AsCollection);
    }

    [Test, CancelAfter(120_000)]
    public async Task ReorderArchetypes_WithAPartialList_ChangesNothing(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);
        await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "partials", ct);

        var all   = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);
        var short_ = all.Values.Select(a => a.id).Take(all.Size - 1).ToArray();

        var result = await Archetypes(scope.ServiceProvider)
           .ReorderArchetypes(spaceId, new IonArray<Guid>(short_), ct);

        Assert.That(result, Is.InstanceOf<FailedReorderArchetypes>());
        Assert.That(((FailedReorderArchetypes)result).error, Is.EqualTo(ArchetypeError.INCOMPLETE_ORDER));

        // Rejected, not half-applied: the roles it did name must not have been ranked.
        var after = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);
        Assert.That(after.Values.Select(a => a.order), Is.All.Null);
    }

    [Test, CancelAfter(120_000)]
    public async Task ReorderArchetypes_WithARepeatedId_IsRefused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);
        await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "dupes", ct);

        var all = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);

        // Right length, wrong contents — the check that counts alone would let through.
        var ids = all.Values.Select(a => a.id).ToArray();
        ids[^1] = ids[0];

        var result = await Archetypes(scope.ServiceProvider)
           .ReorderArchetypes(spaceId, new IonArray<Guid>(ids), ct);

        Assert.That(result, Is.InstanceOf<FailedReorderArchetypes>());
        Assert.That(((FailedReorderArchetypes)result).error, Is.EqualTo(ArchetypeError.INCOMPLETE_ORDER));
    }

    [Test, CancelAfter(120_000)]
    public async Task CreateArchetype_LandsBelowTheRankedOnes(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var all = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);
        await Archetypes(scope.ServiceProvider)
           .ReorderArchetypes(spaceId, new IonArray<Guid>(all.Values.Select(a => a.id).ToArray()), ct);

        var fresh = await Archetypes(scope.ServiceProvider).CreateArchetype(spaceId, "newcomer", ct);

        // A new role carries base entitlements, so ranking it above the existing ones would let it
        // out-rank roles that were deliberately placed.
        var after   = await Archetypes(scope.ServiceProvider).GetServerArchetypes(spaceId, ct);
        var lowest  = after.Values.Where(a => a.id != fresh.id).Max(a => a.order);

        Assert.That(fresh.order, Is.Not.Null);
        Assert.That(fresh.order, Is.GreaterThan(lowest));
    }
}
