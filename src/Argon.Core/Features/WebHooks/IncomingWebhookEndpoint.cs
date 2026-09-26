namespace Argon.Core.Features.WebHooks;

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// <c>POST /api/webhooks/{webhookId}/{token}</c> with <c>{ content, username? }</c>: an external
/// service posts into a channel. The URL is the credential, so an unknown webhook and a wrong token
/// get the same 404.
/// </summary>
public static class IncomingWebhookEndpoint
{
    public const string RoutePath = "/api/webhooks/{webhookId:guid}/{token}";

    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>The URL handed out once; relative when no public base is configured.</summary>
    public static string UrlFor(string? publicBaseUrl, Guid webhookId, string token)
        => $"{(publicBaseUrl ?? "").TrimEnd('/')}/api/webhooks/{webhookId}/{token}";

    public static WebApplication MapIncomingWebhooks(this WebApplication app)
    {
        app.MapPost(RoutePath, ExecuteHandler).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> ExecuteHandler(HttpContext ctx, Guid webhookId, string token, IClusterClient cluster,
        CancellationToken ct)
    {
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
