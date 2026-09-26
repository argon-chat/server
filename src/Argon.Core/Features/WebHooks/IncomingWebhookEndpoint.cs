namespace Argon.Core.Features.WebHooks;

using System.Net.Sockets;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// <c>POST /api/webhooks/{webhookId}/{token}</c> with <c>{ content, username? }</c>: an external
/// service posts into a channel. The URL is the credential, so an unknown webhook and a wrong token
/// get the same 404.
/// </summary>
public static class IncomingWebhookEndpoint
{
    public const string RoutePath = "/api/webhooks/{webhookId:guid}/{token}";

    public const string RateLimitPolicy = "incoming-webhooks";

    /// <summary>Posts one address may make a minute, across every webhook.</summary>
    public const int PerAddressPerMinute = 300;

    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>The URL handed out once; relative when no public base is configured.</summary>
    public static string UrlFor(string? publicBaseUrl, Guid webhookId, string token)
        => $"{(publicBaseUrl ?? "").TrimEnd('/')}/api/webhooks/{webhookId}/{token}";

    public static IServiceCollection AddIncomingWebhookRateLimit(this IServiceCollection services)
        => services.AddRateLimiter(o => o.AddPolicy(RateLimitPolicy, new PerAddressPolicy()));

    public static WebApplication MapIncomingWebhooks(this WebApplication app)
    {
        app.MapPost(RoutePath, ExecuteHandler).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        return app;
    }

    /// <summary>The partition an address falls in: itself, or its /64 for IPv6. Null when there is none.</summary>
    public static string? AddressKey(IPAddress? address)
    {
        if (address is null)
            return null;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    private sealed class PerAddressPolicy : IRateLimiterPolicy<string>
    {
        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = async (rejected, ct) =>
        {
            var response = rejected.HttpContext.Response;
            response.StatusCode         = StatusCodes.Status429TooManyRequests;
            response.Headers.RetryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
                ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)).ToString()
                : "60";
            await response.WriteAsJsonAsync(new { error = "rate_limited" }, ct);
        };

        // Without an address (a test server, a unix socket) nobody is limited, rather than everybody at once.
        public RateLimitPartition<string> GetPartition(HttpContext http)
            => AddressKey(http.Connection.RemoteIpAddress) is { } key
                ? RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PerAddressPerMinute,
                    Window      = TimeSpan.FromMinutes(1),
                    QueueLimit  = 0
                })
                : RateLimitPartition.GetNoLimiter(string.Empty);
    }

    private static async Task<IResult> ExecuteHandler(HttpContext ctx, Guid webhookId, string token, IClusterClient cluster,
        CancellationToken ct)
    {
        // Anything that cannot be an issued token is refused before the cluster is asked.
        if (!ChannelWebhookEntity.IsTokenShaped(token))
            return Error(StatusCodes.Status404NotFound, "unknown_webhook");

        var payload = await ReadPayloadAsync(ctx.Request, ct);
        if (payload is null)
            return Error(StatusCodes.Status400BadRequest, "invalid_payload");

        var result = await cluster.GetGrain<IIncomingWebhookGrain>(webhookId)
           .ExecuteAsync(token, payload.Value.Content, payload.Value.Username);

        switch (result.Outcome)
        {
            case WebhookExecutionOutcome.Accepted:
                return Results.NoContent();
            case WebhookExecutionOutcome.Invalid:
                return Error(StatusCodes.Status400BadRequest, "invalid_payload");
            case WebhookExecutionOutcome.RateLimited:
                ctx.Response.Headers.RetryAfter = Math.Max(1, result.RetryAfterSeconds).ToString();
                return Error(StatusCodes.Status429TooManyRequests, "rate_limited");
            case WebhookExecutionOutcome.Forbidden:
                return Error(StatusCodes.Status403Forbidden, "forbidden");
            default:
                return Error(StatusCodes.Status404NotFound, "unknown_webhook");
        }
    }

    private static IResult Error(int status, string error)
        => Results.Json(new { error }, statusCode: status);

    /// <summary>The body as <c>{ content, username? }</c>, or null when it is not that.</summary>
    private static async Task<(string Content, string? Username)?> ReadPayloadAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes)
            return null;

        using var buffer = new MemoryStream();
        var       chunk  = new byte[8192];
        int       read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        try
        {
            using var json = JsonDocument.Parse(buffer.ToArray());
            var       root = json.RootElement;

            if (root.ValueKind != JsonValueKind.Object
             || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                return null;

            string? username = null;
            if (root.TryGetProperty("username", out var name))
            {
                if (name.ValueKind == JsonValueKind.String)
                    username = name.GetString();
                else if (name.ValueKind != JsonValueKind.Null)
                    return null;
            }

            return (content.GetString()!, username);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
