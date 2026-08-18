namespace Argon.Load.Harness;

using Argon.Load.Client;
using ArgonContracts;

/// <summary>
/// A space with N members in it, seeded against a running server.
/// </summary>
/// <remarks>
/// Every scenario needs the same thing before it can measure anything: a space, an invite, and a
/// crowd inside it. Seeding is itself load — registration hashes a password — so it happens outside
/// whatever window the scenario is timing, and at limited concurrency.
/// <para>
/// Everyone lands in one space on purpose. Spreading the crowd over many spaces spreads it over many
/// grain activations and over many backplane groups, which is a real deployment's shape but not the
/// one that finds contention.
/// </para>
/// </remarks>
public static class SpaceFixture
{
    /// <summary>
    /// Creating a space does not create any channels — <c>ServerRepository.CreateAsync</c> writes the
    /// space, its owner and its archetypes and stops there. So the fixture makes them, and every
    /// scenario has to say how many.
    /// </summary>
    /// <remarks>
    /// This matters more than it looks. A space with no channels makes <c>GetChannels</c> a query
    /// that returns nothing and fans out to nobody, which is the cheapest it will ever be and not
    /// what any real space costs. Measurements taken before the fixture created channels understated
    /// that call, and the fan-out it performs was never exercised at all.
    /// </remarks>
    public static async Task<InviteCode> SeedAsync(
        Uri target, int capacity, string description, int channels, CancellationToken ct)
    {
        using var owner = new LoadClient(target);

        owner.Authenticate(await RegisterAsync(owner, "owner", ct));

        var created = await owner.Service<IUserInteraction>()
           .CreateSpace(new CreateServerRequest($"load-{DateTimeOffset.UtcNow:HHmmss}", description, ""), ct);

        if (created is not SuccessCreateSpace success)
            throw new InvalidOperationException($"could not create the space: {(created as FailedCreateSpace)?.error}");

        var spaceId = success.space.spaceId;

        for (var i = 0; i < channels; i++)
            await owner.Service<IChannelInteraction>().CreateChannel(spaceId, Guid.NewGuid(),
                new CreateChannelRequest(spaceId, $"channel-{i}", ChannelType.Text, "", null), ct);

        var invite = await owner.Service<IServerInteraction>()
           .CreateInviteCode(spaceId, expireMinutes: 120, maxUses: capacity + 1, ct);

        Console.WriteLine($"seeded space {spaceId} with {channels} channel(s), invite {invite.inviteCode}");
        return invite;
    }

    public static async Task<LoadClient> JoinAsync(Uri target, InviteCode invite, int index, CancellationToken ct)
    {
        var client = new LoadClient(target);

        client.Authenticate(await RegisterAsync(client, $"u{index}", ct));
        await client.Service<IUserInteraction>().JoinToSpace(invite, ct);

        return client;
    }

    public static async Task<string> RegisterAsync(LoadClient client, string tag, CancellationToken ct)
    {
        var unique = $"{tag}_{Guid.NewGuid():N}"[..20];

        var result = await client.Service<IIdentityInteraction>().Registration(
            new NewUserCredentialsInput(
                $"{unique}@load.local",
                unique,
                "Load!1234",
                unique,
                true,
                new DateOnly(1996, 6, 6),
                false,
                null,
                "1.0",
                "1.0"),
            ct);

        return result is SuccessRegistration success
            ? success.token
            : throw new InvalidOperationException($"registration failed: {(result as FailedRegistration)?.error}");
    }
}
