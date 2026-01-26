namespace Argon.Core.Features.Transport;

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
public class AppHub(IGrainFactory factory) : Hub
{
    public async override Task OnConnectedAsync()
    {
        EnsureBeforeCall(true);
        var spaceIds = await factory.GetGrain<IUserGrain>(UserId).GetMyServersIds();
        await Task.WhenAll(spaceIds.Select(x => Groups.AddToGroupAsync(Context.ConnectionId, $"spaces/{x}")));
        await factory.GetGrain<IUserSessionGrain>(Context.ConnectionId).BeginRealtimeSession();
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
        => await factory.GetGrain<IUserSessionGrain>(Context.ConnectionId).EndRealtimeSession();

    public async Task SubscribeToSpace(Guid spaceId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"spaces/{spaceId}");

    public async Task UnSubscribeToSpace(Guid spaceId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"spaces/{spaceId}");

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

public class AppHubServer(IHubContext<AppHub> appHub)
{
    public Task BroadcastSpace<T>(T @event, Guid spaceId, CancellationToken ct = default)
        where T : IArgonEvent
    {
        var writer = new CborWriter();
        IonFormatterStorage.GetFormatter<IArgonEvent>().Write(writer, @event);

        return appHub.Clients.Group($"spaces/{spaceId}")
           .SendAsync("broadcastSpace", writer.Encode(), cancellationToken: ct);
    }

    public Task ForUser<T>(T @event, Guid userId, CancellationToken ct = default)
        where T : IArgonEvent
    {
        var writer = new CborWriter();
        IonFormatterStorage.GetFormatter<IArgonEvent>().Write(writer, @event);
        return appHub.Clients.User(userId.ToString())
           .SendAsync("forSelf", writer.Encode(), cancellationToken: ct);
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
           .AddSingleton<IUserIdProvider, GuidUserIdProvider>()
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
        if (!Request.Path.StartsWithSegments("/w"))
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