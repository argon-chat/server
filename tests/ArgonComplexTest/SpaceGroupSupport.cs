namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The set-up the space-grain fixtures share: a space, a member in it, a role for that member, and a
/// way to call a grain the way the Ion interceptor would.
/// </summary>
/// <remarks>
/// Every helper takes the <see cref="TestUserSession"/> it acts as, rather than leaning on a fixture's
/// ambient token, because nearly every test here is about who may do something to a space — two
/// identities in one test are the rule, not the exception.
/// </remarks>
internal static class SpaceGroupSupport
{
    private static IServiceProvider Services => ArgonTestEnvironment.Instance.Host.Services;

    public static IGrainFactory Grains => Services.GetRequiredService<IGrainFactory>();

    public static IArchetypeInteraction Archetypes(TestUserSession session)
        => session.Client.ForService<IArchetypeInteraction>(Services);

    public static IBotManagementInteraction Bots(TestUserSession session)
        => session.Client.ForService<IBotManagementInteraction>(Services);

    public static IUltimaInteraction Ultima(TestUserSession session)
        => session.Client.ForService<IUltimaInteraction>(Services);

    public static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name = "Space", CancellationToken ct = default)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Description", string.Empty), ct);

        Assert.That(result, Is.InstanceOf<SuccessCreateSpace>(),
            $"could not create the space: {(result as FailedCreateSpace)?.error}");

        return ((SuccessCreateSpace)result).space.spaceId;
    }

    public static async Task JoinAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct = default)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(), $"the guest could not join: {(joined as FailedJoin)?.error}");
    }

    /// <summary>Gives <paramref name="userId"/> a role of its own carrying exactly <paramref name="entitlements"/>.</summary>
    public static async Task<Archetype> GrantAsync(
        TestUserSession owner, Guid spaceId, Guid userId, ArgonEntitlement entitlements, CancellationToken ct = default)
    {
        var archetypes = Archetypes(owner);

        var role = await archetypes.CreateArchetype(spaceId, $"role-{Guid.NewGuid():N}"[..16], ct).Ok();
        role = await archetypes.UpdateArchetype(spaceId, role with { entitlement = entitlements }, ct).Ok();

        var member  = await owner.Servers.GetMember(spaceId, userId, ct);
        var granted = await archetypes.SetArchetypeToMember(spaceId, member.member.memberId, role.id, true, ct);

        Assert.That(granted, Is.True, "the owner could not hand out a role in their own space");
        return role;
    }

    /// <summary>The channels a member of the space sees, in the order the client draws them.</summary>
    public static async Task<List<ArgonChannel>> ChannelsAsync(TestUserSession viewer, Guid spaceId, CancellationToken ct = default)
        => (await viewer.Channels.GetChannels(spaceId, Guid.Empty, ct)).Values
           .Select(c => c.channel)
           .OrderBy(c => c.groupId)
           .ThenBy(c => c.fractionalIndex, StringComparer.Ordinal)
           .ToList();

    /// <summary>The channel groups of the space, in the order the client draws them.</summary>
    public static async Task<List<ChannelGroup>> GroupsAsync(TestUserSession viewer, Guid spaceId, CancellationToken ct = default)
    {
        var snapshot = await viewer.Servers.GetSpaceSnapshot(spaceId, null, ct);

        return snapshot.groups!.Value.Values
           .OrderBy(g => g.fractionalIndex, StringComparer.Ordinal)
           .ToList();
    }

    public static async Task<Guid> CreateChannelAsync(
        TestUserSession actor, Guid spaceId, string name, ChannelType kind = ChannelType.Text, Guid? groupId = null,
        CancellationToken ct = default)
    {
        await actor.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, name, kind, "space group fixture", groupId), ct).Ok();

        var created = (await ChannelsAsync(actor, spaceId, ct)).Where(c => c.name == name).ToList();

        Assert.That(created, Has.Count.EqualTo(1), $"expected exactly one channel named '{name}'");
        return created[0].channelId;
    }

    public static async Task<Guid> CreateGroupAsync(TestUserSession actor, Guid spaceId, string name, CancellationToken ct = default)
    {
        await actor.Channels.CreateChannelGroup(spaceId, Guid.Empty, name, null, ct).Ok();

        var created = (await GroupsAsync(actor, spaceId, ct)).Where(g => g.name == name).ToList();

        Assert.That(created, Has.Count.EqualTo(1), $"expected exactly one group named '{name}'");
        return created[0].groupId;
    }

    public static async Task<ApplicationDbContext> NewDbAsync(CancellationToken ct = default)
        => await Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync(ct);

    public static ISpaceGrain SpaceGrain(Guid spaceId) => Grains.GetGrain<ISpaceGrain>(spaceId);

    /// <summary>A grain call made as <paramref name="caller"/> — the one fact the Ion interceptor puts on every call.</summary>
    public static async Task<T> AsCallerAsync<T>(Guid caller, Func<Task<T>> call)
    {
        RequestContext.Set("$caller_user_id", caller);

        try
        {
            return await call();
        }
        finally
        {
            RequestContext.Remove("$caller_user_id");
        }
    }

    public static Task AsCallerAsync(Guid caller, Func<Task> call)
        => AsCallerAsync(caller, async () =>
        {
            await call();
            return true;
        });

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    /// <summary>A one-pixel PNG, so anything that sniffs the upload sees a real image.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>Puts the bytes where the ticket says, exactly as signed — see <c>MediaUploadTests.Upload</c>.</summary>
    public static async Task UploadAsync(SuccessUploadFile ticket, byte[] payload, string contentType = "image/png")
    {
        var port = int.Parse(ArgonTestEnvironment.Instance.S3Endpoint.Split(':')[1]);

        using var client = new HttpClient(new SocketsHttpHandler
        {
            // The signed host is a name nothing resolves; dial the store's mapped port instead.
            ConnectCallback = async (_, token) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                await socket.ConnectAsync(System.Net.IPAddress.Loopback, port, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
        });

        using var content = new ByteArrayContent(payload);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        foreach (var field in ticket.formFields)
            content.Headers.TryAddWithoutValidation(field.key, field.value);

        using var response = await client.PutAsync(ticket.uploadUrl, content);

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK),
            $"the object store refused the presigned upload: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// An id <c>SpaceGrain</c> treats as a meeting guest — the first four bytes are the guest prefix.
    /// </summary>
    public static Guid GuestId()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        bytes[0] = 0xFA;
        bytes[1] = 0xFC;
        bytes[2] = 0xCC;
        bytes[3] = 0xCC;
        return new Guid(bytes);
    }
}
