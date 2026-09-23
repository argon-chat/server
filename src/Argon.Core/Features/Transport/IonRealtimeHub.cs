namespace Argon.Core.Features.Transport;

using Argon.Features.Auth;
using Argon.Services;
using ion.runtime;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Runtime.CompilerServices;

/// <summary>
/// <c>EventBus.Realtime</c>: the Ion stream counterpart of <see cref="AppHub"/>, one scoped instance per
/// connection. The Ion ticket is the identity; a resumed session runs no hooks and keeps its connection id.
/// </summary>
public sealed class IonRealtimeHub(
    IGrainFactory factory,
    IRealtimeReplayBuffer replay,
    HybridCache cache,
    IArgonCacheDatabase cacheDb,
    HubConnectionRegistry registry,
    ILogger<IonRealtimeHub> logger)
{
    private const string AttachedItem = "argon.attached";

    // SESSION_REVOKED is final for the client; any other failure closes with an invitation back.
    public async Task OnConnectedAsync(IIonStreamContext context)
    {
        if (context.Ticket is not ArgonIonTicket ticket)
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));

        ApplyCallerContext(ticket);

        try
        {
            // Uncached, as the hub reads it on connect.
            if (await SessionRevocation.IsRevokedOrThrowAsync(
                    cacheDb, null, ticket.userId, Identities(ticket), ticket.issuedAt))
                throw SignedOut();

            // Queued first, so group pushes land behind it; nothing queued here leaves unless the hook succeeds.
            await context.SendAsync<IRealtimeFrame>(new Welcome(context.ConnectionId));

            context.UserIdentifier = ticket.userId.ToString();

            var spaceIds = await factory.GetGrain<IUserGrain>(ticket.userId).GetMyServersIds();
            foreach (var spaceId in spaceIds)
                await context.AddToGroupAsync($"spaces/{spaceId}");

            if (!await SessionGrain(ticket).AttachConnectionAsync(context.ConnectionId))
                throw SignedOut();
        }
        catch (Exception e) when (e is not IonRequestException)
        {
            logger.LogWarning(e, "Could not accept realtime connection {ConnectionId} of user {UserId}; asking it to retry",
                context.ConnectionId, ticket.userId);
            context.Close("unavailable", allowReconnect: true);
            return;
        }

        context.Items[AttachedItem] = true;

        registry.Attach(new HubConnectionEntry(
            context.ConnectionId, ticket.userId, ticket.sessionId, [.. ticket.credentialSessionIds],
            ticket.issuedAt, new IonRealtimeConnection(context)));
    }

    public async Task OnDisconnectedAsync(IIonStreamContext context)
    {
        registry.Detach(context.ConnectionId);

        if (!context.Items.ContainsKey(AttachedItem) || context.Ticket is not ArgonIonTicket ticket)
            return;

        await SessionGrain(ticket).DetachConnectionAsync(context.ConnectionId);
    }

    // A replay is yielded, not pushed: it can outgrow the push queue, and yielded frames are paced by
    // the socket. Resumed follows its replay on the same path.
    public async IAsyncEnumerable<IRealtimeFrame> RunAsync(
        IIonStreamContext context, IAsyncEnumerable<IRealtimeCommand>? commands,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (context.Ticket is ArgonIonTicket ticket && commands is not null)
        {
            await using var input = commands.GetAsyncEnumerator(ct);

            while (await NextAsync(context, input, ct) is { } command)
            {
                foreach (var frame in await HandleAsync(context, ticket, command, ct))
                    yield return frame;
            }
        }

        await foreach (var frame in IonStream.PushOnly<IRealtimeFrame>(ct))
            yield return frame;
    }

    private async Task<IRealtimeCommand?> NextAsync(
        IIonStreamContext context, IAsyncEnumerator<IRealtimeCommand> input, CancellationToken ct)
    {
        try
        {
            return await input.MoveNextAsync() ? input.Current : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e)
        {
            // Undecodable input ends the input for good, so the connection goes and the client starts afresh.
            logger.LogWarning(e, "Realtime connection {ConnectionId} sent a command that could not be read",
                context.ConnectionId);
            context.Close("unreadable command", allowReconnect: true);
            return null;
        }
    }

    private async Task<IReadOnlyList<IRealtimeFrame>> HandleAsync(
        IIonStreamContext context, ArgonIonTicket ticket, IRealtimeCommand command, CancellationToken ct)
    {
        ApplyCallerContext(ticket);

        try
        {
            switch (command)
            {
                case ArgonContracts.Resume resume:
                    return await ResumeAsync(context, ticket, resume, ct);
                case ArgonContracts.Heartbeat heartbeat:
                    await HeartbeatAsync(context, ticket, heartbeat.status);
                    break;
                case ArgonContracts.GoOffline:
                    if (await EnsureSessionIsLiveAsync(context, ticket, markSeen: false))
                        await SessionGrain(ticket).GoOfflineAsync(context.ConnectionId);
                    break;
                case SubscribeToChannel subscribe:
                    await SubscribeToChannelAsync(context, ticket, subscribe.channelId);
                    break;
                case UnsubscribeFromChannel unsubscribe:
                    MarkSeen(context, ticket);
                    await context.RemoveFromGroupAsync($"channels/{unsubscribe.channelId}", ct);
                    break;
                case ArgonContracts.Typing typing:
                    if (await EnsureSessionIsLiveAsync(context, ticket))
                        await factory.GetGrain<IChannelGrain>(typing.channelId).OnTypingEmit();
                    break;
                case StopTyping stopTyping:
                    if (await EnsureSessionIsLiveAsync(context, ticket))
                        await factory.GetGrain<IChannelGrain>(stopTyping.channelId).OnTypingStopEmit();
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Realtime command {Command} failed on connection {ConnectionId}",
                command.UnionKey, context.ConnectionId);
        }

        return [];
    }

    /// <inheritdoc cref="AppHub.Resume"/>
    private async Task<IReadOnlyList<IRealtimeFrame>> ResumeAsync(
        IIonStreamContext context, ArgonIonTicket ticket, ArgonContracts.Resume resume, CancellationToken ct)
    {
        if (!await EnsureSessionIsLiveAsync(context, ticket))
            return [];

        var frames         = new List<IRealtimeFrame>();
        var needFullResync = false;

        var userResult = await replay.ReadUserSinceAsync(ticket.userId, resume.userCursor, ct);
        if (userResult.Gap)
            needFullResync = true;
        foreach (var e in userResult.Entries)
            frames.Add(new ForSelf(e.Payload, e.Id));

        if (resume.spaceCursors is { Count: > 0 } cursors)
        {
            // Only spaces the user is still in: membership may have changed during the gap.
            var mySpaces = (await factory.GetGrain<IUserGrain>(ticket.userId).GetMyServersIds()).ToHashSet();

            foreach (var cursor in cursors)
            {
                if (!mySpaces.Contains(cursor.spaceId))
                    continue;

                var spaceResult = await replay.ReadSpaceSinceAsync(cursor.spaceId, cursor.entryId, ct);
                if (spaceResult.Gap)
                {
                    needFullResync = true;
                    continue;
                }

                foreach (var e in spaceResult.Entries)
                    frames.Add(new ForSpace(e.Payload, cursor.spaceId, e.Id));
            }
        }

        frames.Add(new Resumed(needFullResync));
        return frames;
    }

    /// <inheritdoc cref="AppHub.Heartbeat"/>
    private async Task HeartbeatAsync(IIonStreamContext context, ArgonIonTicket ticket, UserStatus status)
    {
        if (!await EnsureSessionIsLiveAsync(context, ticket, markSeen: false))
            return;

        if (!await SessionGrain(ticket).HeartBeatAsync(context.ConnectionId, status))
            await SignOutAsync(context);
    }

    /// <inheritdoc cref="AppHub.SubscribeToChannel"/>
    private async Task SubscribeToChannelAsync(IIonStreamContext context, ArgonIonTicket ticket, Guid channelId)
    {
        if (!await EnsureSessionIsLiveAsync(context, ticket))
            return;

        if (await factory.GetGrain<IUserGrain>(ticket.userId).ResolveChannelSpaceIfMemberAsync(channelId) is null)
        {
            logger.LogInformation("Realtime connection {ConnectionId} of user {UserId} was refused channel {ChannelId}",
                context.ConnectionId, ticket.userId, channelId);
            return;
        }

        await context.AddToGroupAsync($"channels/{channelId}");
    }

    private async Task<bool> EnsureSessionIsLiveAsync(IIonStreamContext context, ArgonIonTicket ticket, bool markSeen = true)
    {
        if (await SessionRevocation.IsRevokedAsync(
                cacheDb, cache, ticket.userId, Identities(ticket), ticket.issuedAt, failClosed: false, logger))
        {
            await SignOutAsync(context);
            return false;
        }

        if (markSeen)
            MarkSeen(context, ticket);

        return true;
    }

    private static IReadOnlyList<Guid> Identities(ArgonIonTicket ticket)
        => [ticket.sessionId, .. ticket.credentialSessionIds];

    private async Task SignOutAsync(IIonStreamContext context)
    {
        var connection = new IonRealtimeConnection(context);

        await connection.SayGoodbyeAsync(HubConnectionRegistry.SignedOutReason, CancellationToken.None);
        connection.Close(HubConnectionRegistry.SignedOutReason);
    }

    private void MarkSeen(IIonStreamContext context, ArgonIonTicket ticket)
        => _ = SessionGrain(ticket).MarkConnectionSeenAsync(context.ConnectionId);

    private IUserSessionGrain SessionGrain(ArgonIonTicket ticket)
        => factory.GetGrain<IUserSessionGrain>($"{ticket.userId}:{ticket.sessionId}");

    private static void ApplyCallerContext(ArgonIonTicket ticket)
    {
        var section = RequestContext.AllowCallChainReentrancy();

        section.SetUserId(ticket.userId);
        section.SetUserMachineId(ticket.machineId);
        section.SetUserSessionId(ticket.sessionId);
    }

    private static IonRequestException SignedOut()
        => new(new IonProtocolError("SESSION_REVOKED", "this session has been signed out"));
}

public static class IonRealtimeHubExtensions
{
    public static void AddIonRealtimeEndpoint(this WebApplicationBuilder builder)
    {
        builder.AddRealtimeConnectionTracking();
        builder.Services.TryAddScoped<IonRealtimeHub>();
    }
}
