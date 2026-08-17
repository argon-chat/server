namespace Argon.Services.Ion;

using Features.AccountConsole;
using ion.runtime;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Ion interceptor for the developer account console. Validates the caller's token against the
/// OAuth provider's JWKS and publishes the request context the console's services read.
/// </summary>
/// <remarks>
/// The key set is fetched and refreshed by <see cref="ConfigurationManager{T}"/> — the same
/// machinery <see cref="OperatorAuthInterceptor"/> uses — rather than by a hand-rolled cache with a
/// fixed lifetime, so a rotated signing key is picked up instead of failing every request for a day.
/// </remarks>
public sealed class AccountConsoleAuthInterceptor(
    ILogger<AccountConsoleAuthInterceptor> logger,
    IOptions<AccountConsoleAuthOptions> options)
    : IIonInterceptor
{
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> configManager = new(() =>
        new ConfigurationManager<OpenIdConnectConfiguration>(
            options.Value.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever()));

    private static readonly JsonWebTokenHandler TokenHandler = new();

    public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.MetadataAddress) || string.IsNullOrWhiteSpace(options.Value.ValidIssuer))
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Account console auth is not configured"));

        var accessor    = context.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var httpContext = accessor.HttpContext ?? throw new InvalidOperationException("HttpContext is not available");

        var token = ExtractBearerToken(httpContext);

        if (token is null)
        {
            logger.LogWarning("No authorization header was supplied, returning NO_AUTH");
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));
        }

        var configuration = await configManager.Value.GetConfigurationAsync(ct);

        var result = await TokenHandler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer              = options.Value.ValidIssuer,
            ValidAudiences           = options.Value.ValidAudiences,
            ValidateAudience         = options.Value.ValidAudiences.Count > 0,
            IssuerSigningKeys        = configuration.SigningKeys,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true
        });

        if (!result.IsValid)
        {
            logger.LogWarning("Invalid console token from IP={Ip}: {Error}",
                httpContext.Connection.RemoteIpAddress, result.Exception?.Message);
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Invalid or expired token"));
        }

        var claims = result.ClaimsIdentity;

        if (claims.FindFirst("sub")?.Value is not { } subject || !Guid.TryParse(subject, out var userId))
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Token carries no usable subject"));

        var headers = context.RequestItems;

        string Header(string name, string fallback)
            => headers.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;

        ArgonRequestContext.Set(new ArgonRequestContextData
        {
            Ip         = Header("CF-Connecting-IP", "unknown"),
            Region     = Header("CF-IPCountry", "unknown"),
            Ray        = Header("CF-Ray", Guid.NewGuid().ToString()),
            ClientName = Header("User-Agent", "unknown"),
            SessionId  = default,
            MachineId  = default,
            AppId      = default,
            UserId     = userId,
            Scope      = context.ServiceProvider,
            Props =
            {
                ["displayName"] = claims.FindFirst("displayName")?.Value ?? "Unknown User",
                ["avatarId"]    = claims.FindFirst("avatarFileId")?.Value ?? ""
            }
        });

        await next(context, ct);
    }

    private static string? ExtractBearerToken(HttpContext httpContext)
    {
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var auth) || string.IsNullOrWhiteSpace(auth))
            return null;

        var value = auth.ToString();

        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value["Bearer ".Length..].Trim()
            : value.Trim();
    }
}
