namespace ArgonComplexTest.Tests;

using ArgonContracts;
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
}
