namespace Argon.Core.Features.Transport;

using Argon.Features.BotApi;
using Argon.Features.Env;
using ion.runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using StackExchange.Redis;
using System.Formats.Cbor;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;

[Authorize(AuthenticationSchemes = "Ticket", Policy = "ticket")]
public class AppHub(IGrainFactory factory, IRealtimeReplayBuffer replay) : Hub
{
    public async override Task OnConnectedAsync()
    {
        EnsureBeforeCall(true);
        var spaceIds = await factory.GetGrain<IUserGrain>(UserId).GetMyServersIds();
        await Task.WhenAll(spaceIds.Select(x => Groups.AddToGroupAsync(Context.ConnectionId, $"spaces/{x}")));
        await factory.GetGrain<IUserSessionGrain>(Context.ConnectionId).BeginRealtimeSession();
    }

    /// <summary>
    /// Replay events the client missed while it was briefly disconnected.
    ///
    /// The client passes the last entry id it saw on its personal (<c>forSelf</c>) stream and
    /// on each subscribed space (<c>broadcastSpace</c>) stream. We re-send everything after
    /// those cursors through the normal client handlers (the client dedupes by id). If any
    /// cursor is too old to guarantee continuity we set <see cref="ResumeAck.NeedFullResync"/>
    /// so the client reloads its state from scratch instead of trusting a partial replay.
    /// </summary>
    public async Task<ResumeAck> Resume(string? userCursor, Dictionary<string, string>? spaceCursors)
    {
        EnsureBeforeCall(true);

        var needFullResync = false;

        var userResult = await replay.ReadUserSinceAsync(UserId, userCursor);
        if (userResult.Gap)
            needFullResync = true;
        foreach (var e in userResult.Entries)
            await Clients.Caller.SendAsync("forSelf", e.Payload, e.Id);

        if (spaceCursors is { Count: > 0 })
        {
            // Only replay spaces the user is still a member of — membership may have changed
            // during the gap, and we must not leak events from spaces they no longer belong to.
            var mySpaces = (await factory.GetGrain<IUserGrain>(UserId).GetMyServersIds()).ToHashSet();

            foreach (var (spaceIdRaw, cursor) in spaceCursors)
            {
                if (!Guid.TryParse(spaceIdRaw, out var spaceId) || !mySpaces.Contains(spaceId))
                    continue;

                var spaceResult = await replay.ReadSpaceSinceAsync(spaceId, cursor);
                if (spaceResult.Gap)
                {
                    needFullResync = true;
                    continue;
                }

                foreach (var e in spaceResult.Entries)
                    await Clients.Caller.SendAsync("broadcastSpace", e.Payload, spaceId, e.Id);
            }
        }

        return new ResumeAck(needFullResync);
    }

    private Guid UserId => Guid.Parse(Context.UserIdentifier!);

    private void EnsureBeforeCall(bool isAllowAbort = false)
    {
        bool takeClaim(string key, out string value)
        {
            value = "";
            var kv = Context.User?.FindFirst(key);
            if (kv is null && isAllowAbort)
            {
                Context.Abort();
                return false;
            }

            if (kv is null)
                throw new InvalidOperationException($"invalid operations, claim '{key}' is not found in user ticket");
            value = kv.Value;
            return true;
        }


        var userId = Context.UserIdentifier!;
        if (!takeClaim("sid", out var sessionId))
            return;
        if (!takeClaim("mid", out var machineId))
            return;

        RequestContext.AllowCallChainReentrancy();
        this.SetUserId(Guid.Parse(userId));
        this.SetUserMachineId(machineId);
        this.SetUserSessionId(Guid.Parse(sessionId));
    }

    public async override Task OnDisconnectedAsync(Exception? exception)
        => await factory.GetGrain<IUserSessionGrain>(Context.ConnectionId).MarkDisconnectedAsync();

    public async Task SubscribeToSpace(Guid spaceId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"spaces/{spaceId}");

    public async Task UnSubscribeToSpace(Guid spaceId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"spaces/{spaceId}");

    // Channel-scoped subscription: the client joins only the channel it currently has open, so
    // channel content (messages/typing/reactions) is delivered to viewers instead of the whole space.
    public async Task SubscribeToChannel(Guid channelId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"channels/{channelId}");

    public async Task UnSubscribeToChannel(Guid channelId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"channels/{channelId}");

    public async Task Heartbeat(UserStatus status)
        => await factory.GetGrain<IUserSessionGrain>(Context.ConnectionId).HeartBeatAsync(status);

    public async Task IAmTyping(Guid channelId)
    {
        EnsureBeforeCall(true);
        await factory.GetGrain<IChannelGrain>(channelId).OnTypingEmit();
    }

    public async Task IAmStopTyping(Guid channelId)
    {
        EnsureBeforeCall(true);
        await factory.GetGrain<IChannelGrain>(channelId).OnTypingStopEmit();
    }
}

/// <summary>Result of <see cref="AppHub.Resume"/>. Serialized to the client over the hub protocol.</summary>
public sealed record ResumeAck(bool NeedFullResync);

public class AppHubServer(
    IHubContext<AppHub> appHub,
    BotEventPublisher botEventPublisher,
    IRealtimeReplayBuffer replay,
    ILogger<AppHubServer> logger)
{
    public async Task BroadcastSpace<T>(T @event, Guid spaceId, CancellationToken ct = default)
        where T : IArgonEvent
    {
        var writer = new CborWriter();
        IonFormatterStorage.GetFormatter<IArgonEvent>().Write(writer, @event);
        var payload = writer.Encode();

        // Persist to the replay log first so the cursor (entry id) we hand the client is
        // durable: if it reconnects it can ask for everything after this id.
        var entryId = await replay.AppendSpaceAsync(spaceId, payload, ct);

        await appHub.Clients.Group($"spaces/{spaceId}")
           .SendAsync("broadcastSpace", payload, spaceId, entryId, cancellationToken: ct);

        // Publish to NATS for bots — single publish, bots consume independently
        _ = botEventPublisher.PublishIfMappedAsync(@event, spaceId);
    }

    /// <summary>
    /// Channel-scoped delivery for high-frequency channel content (messages, typing, reactions).
    /// Only clients currently viewing the channel join its group, so a message fans out to channel
    /// viewers — not to all N members of the space. Missed messages on a brief disconnect are
    /// recovered by the client re-fetching the open channel's recent history on reconnect (messages
    /// are persisted; reactions load with them; typing is ephemeral), so there is no replay stream.
    /// </summary>
    public async Task BroadcastChannel<T>(T @event, Guid spaceId, Guid channelId, CancellationToken ct = default)
        where T : IArgonEvent
    {
        var writer = new CborWriter();
        IonFormatterStorage.GetFormatter<IArgonEvent>().Write(writer, @event);
        var payload = writer.Encode();

        await appHub.Clients.Group($"channels/{channelId}")
           .SendAsync("broadcastChannel", payload, channelId, cancellationToken: ct);

        // Bots are mapped per-space (not per-channel), so channel content still reaches them through
        // the existing space NATS mapping exactly as before.
        _ = botEventPublisher.PublishIfMappedAsync(@event, spaceId);
    }

    public async Task ForUser<T>(T @event, Guid userId, CancellationToken ct = default)
        where T : IArgonEvent
    {
        var writer = new CborWriter();
        IonFormatterStorage.GetFormatter<IArgonEvent>().Write(writer, @event);
        var payload = writer.Encode();

        var entryId = await replay.AppendUserAsync(userId, payload, ct);

        await appHub.Clients.User(userId.ToString())
           .SendAsync("forSelf", payload, entryId, cancellationToken: ct);

        // Publish to NATS for bots (calls, DMs)
        _ = botEventPublisher.PublishForUserAsync(@event, userId);
    }
}

public sealed class GuidUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
        => connection.User?.FindFirstValue(ClaimTypes.NameIdentifier);
}

public static class SignalRHubExtensions
{
    public static IHubContext<AppHub> GetAppHubContext(this IServiceProvider serviceProvider)
        => serviceProvider.GetRequiredService<IHubContext<AppHub>>();

    public static void AddSignalRAppHub(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsEntryPoint() || builder.Environment.IsHybrid())
        {
            builder.Services.AddAuthentication()
               .AddScheme<AuthenticationSchemeOptions, TicketAuthHandler>("Ticket", _ => { });

            builder.Services.AddAuthorization(o =>
            {
                o.AddPolicy("ticket", policy =>
                {
                    policy.AddAuthenticationSchemes("Ticket");
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim("typ", "ticket");
                });
            });
        }

        builder.Services
           .AddSingleton<IRealtimeReplayBuffer, RedisRealtimeReplayBuffer>()
           .AddSingleton<IUserIdProvider, GuidUserIdProvider>()
           .AddSingleton<Argon.Features.BotApi.BotSseEventSerializer>()
           .AddSingleton<Argon.Features.BotApi.BotUserCache>()
           .AddSingleton<Argon.Features.BotApi.UserLocaleRegistry>()
           .AddSingleton<Argon.Features.BotApi.InteractionContextStore>()
           .AddSingleton<Argon.Features.BotApi.BotEventPublisher>()
           .AddScoped<Argon.Features.BotApi.InteractionResponsePusher>()
           .AddScoped<AppHubServer>()
           .AddSignalR()
           //.AddMessagePackProtocol()
           .AddHubOptions<AppHub>(options => options.EnableDetailedErrors = true)
           .AddStackExchangeRedis(builder.Configuration.GetConnectionString("cache")!,
                x => x.Configuration.ChannelPrefix = new RedisChannel("argon-bus", RedisChannel.PatternMode.Literal));
    }
}

public sealed class TicketAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments("/w") && !Request.Path.StartsWithSegments("/api/spaces"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = Request.Query["access_token"].ToString();

        if (string.IsNullOrEmpty(token))
        {
            var          auth   = Request.Headers[HeaderNames.Authorization].ToString();
            const string prefix = "Bearer ";
            if (auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                token = auth[prefix.Length..].Trim();
        }

        if (string.IsNullOrEmpty(token))
            return Task.FromResult(AuthenticateResult.NoResult());

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(configuration["TicketJwt:Key"]!));

        var tvp = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer    = "ticket.argon.gl",

            ValidateAudience = true,
            ValidAudience    = "ticket.argon.gl",

            ValidateLifetime = true,
            ClockSkew        = TimeSpan.FromSeconds(10),

            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = key,
        };

        try
        {
            var handler   = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, tvp, out var validatedToken);

            // typ=ticket обязательно
            var typ = principal.FindFirst("typ")?.Value;
            if (!string.Equals(typ, "ticket", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.Fail("Not a ticket token"));

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(AuthenticateResult.Fail(ex));
        }
    }
}