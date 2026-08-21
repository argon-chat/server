namespace Argon.Services.Ion;

using Features.Admin;
using ion.runtime;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Ion interceptor for admin API endpoints.
/// Validates operator JWT tokens via JWKS from the Aegis OAuth provider.
/// </summary>
public sealed class OperatorAuthInterceptor(
    ILogger<OperatorAuthInterceptor> logger,
    IOptions<OperatorAuthOptions> options)
    : IIonInterceptor
{
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> _configManager = new(() =>
        new ConfigurationManager<OpenIdConnectConfiguration>(
            options.Value.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever()));

    private static readonly JsonWebTokenHandler TokenHandler = new();

    public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.MetadataAddress) || string.IsNullOrWhiteSpace(options.Value.ValidIssuer))
            throw new IonRequestException(new IonProtocolError("NO_OPERATOR_AUTH", "Operator auth is not configured"));

        var httpAccessor = context.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var httpContext   = httpAccessor.HttpContext
                           ?? throw new InvalidOperationException("HttpContext is not available");

        var token = ExtractBearerToken(httpContext);
        if (token is null)
            throw new IonRequestException(new IonProtocolError("NO_OPERATOR_AUTH", "Operator authorization required"));

        var config = await _configManager.Value.GetConfigurationAsync(ct);

        var validationParams = new TokenValidationParameters
        {
            ValidIssuer            = options.Value.ValidIssuer,
            ValidateAudience       = false,
            IssuerSigningKeys      = config.SigningKeys,
            ValidateLifetime       = true,
            ValidateIssuerSigningKey = true,
        };

        var result = await TokenHandler.ValidateTokenAsync(token, validationParams);
        if (!result.IsValid)
        {
            logger.LogWarning("Invalid operator token from IP={Ip}: {Error}",
                httpContext.Connection.RemoteIpAddress, result.Exception?.Message);
            throw new IonRequestException(new IonProtocolError("NO_OPERATOR_AUTH", "Invalid or expired operator token"));
        }

        var claims = result.ClaimsIdentity;

        if (claims.FindFirst("typ")?.Value != "operator")
            throw new IonRequestException(new IonProtocolError("NO_OPERATOR_AUTH", "Token is not an operator token"));

        OperatorRequestContext.Set(new OperatorRequestContextData
        {
            UserId                = Guid.Parse(claims.FindFirst("sub")!.Value),
            OperatorId            = Guid.Parse(claims.FindFirst("operator_id")!.Value),
            Email                 = claims.FindFirst("operator_email")!.Value,
            CertificateThumbprint = claims.FindFirst("operator_cert_thumbprint")!.Value,
        });

        await next(context, ct);
    }

    private static string? ExtractBearerToken(HttpContext httpContext)
    {
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var auth) || string.IsNullOrWhiteSpace(auth))
            return null;

        var value = auth.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        return value["Bearer ".Length..].Trim();
    }
}
