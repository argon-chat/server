namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Sockets;
using Argon.Core.Features.Logic;
using Argon.Entities;
using ArgonComplexTest.Infrastructure;
using ArgonComplexTest.Infrastructure.Account;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What the social fixtures (friends, direct chats, calls, saved GIFs, privacy, profile) share.
/// </summary>
public static class SocialHarness
{
    public static IServiceProvider Services => ArgonTestEnvironment.Instance.Host.Services;

    public static IGrainFactory Grains => Services.GetRequiredService<IGrainFactory>();

    public static Task<ApplicationDbContext> DbAsync(CancellationToken ct = default) => AccountSeed.NewDbAsync(ct);

    /// <summary>
    /// A live realtime stream for <paramref name="session"/>, returned once the server lists the
    /// session — which is what every <c>NotifyAsync</c> in the social grains addresses.
    /// </summary>
    public static async Task<IonRealtimeClient> OnlineAsync(TestUserSession session, CancellationToken ct = default)
    {
        var stream = await IonRealtimeClient.ConnectAsync(session, ct);

        await stream.Heartbeat(UserStatus.Online);
        await stream.BarrierAsync(ct);

        var discovery = Services.GetRequiredService<IUserSessionDiscoveryService>();
        var listed = await Poll.UntilAsync(
            async () => (await discovery.GetUserSessionsAsync(session.UserId, ct)).Count > 0,
            TimeSpan.FromSeconds(15), ct: ct);

        Assert.That(listed, Is.True, $"the stream never registered a session: {stream.Dump()}");
        return stream;
    }

    /// <summary>Runs a direct grain call as <paramref name="userId"/>, the way the Ion layer does.</summary>
    public static async Task<T> AsCallerAsync<T>(Guid userId, Func<Task<T>> call)
    {
        Orleans.Runtime.RequestContext.Set("$caller_user_id", userId);
        try
        {
            return await call();
        }
        finally
        {
            Orleans.Runtime.RequestContext.Clear();
        }
    }

    /// <inheritdoc cref="AsCallerAsync{T}(Guid,Func{Task{T}})"/>
    public static Task AsCallerAsync(Guid userId, Func<Task> call)
        => AsCallerAsync(userId, async () =>
        {
            await call();
            return true;
        });

    /// <summary>
    /// Puts <paramref name="payload"/> where a presigned upload ticket points, applying the signed
    /// fields verbatim — the same request a client makes.
    /// </summary>
    public static async Task UploadAsync(SuccessUploadFile ticket, byte[] payload, string contentType)
    {
        using var client  = DirectToStore();
        using var content = new ByteArrayContent(payload);

        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        foreach (var field in ticket.formFields)
            content.Headers.TryAddWithoutValidation(field.key, field.value);

        using var response = await client.PutAsync(ticket.uploadUrl, content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"the object store refused the presigned upload: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>A one-pixel PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>Dials the store's mapped port whatever host the signed URL names (see MediaUploadTests).</summary>
    private static HttpClient DirectToStore()
    {
        var port = int.Parse(ArgonTestEnvironment.Instance.S3Endpoint.Split(':')[1]);

        return new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(IPAddress.Loopback, port, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
        });
    }
}
