namespace Argon.Services.Ion;

using Argon.Features.Admin;
using ion.runtime;

/// <summary>
/// Ion interceptor for admin API endpoints.
/// Validates operator JWT tokens (issued after PIV challenge-response auth).
/// NOT registered in the main AddIonProtocol() — will be connected to the admin endpoint when the mechanism is ready.
/// </summary>
public sealed class OperatorAuthInterceptor(OperatorJwtService jwtService, ILogger<OperatorAuthInterceptor> logger)
    : IIonInterceptor
{
    public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        var httpAccessor = context.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var httpContext   = httpAccessor.HttpContext
                           ?? throw new InvalidOperationException("HttpContext is not available");

        var token = ExtractBearerToken(httpContext);
        if (token is null)
            throw new IonRequestException(new IonProtocolError("NO_OPERATOR_AUTH", "Operator authorization required"));

        var operatorData = jwtService.ValidateToken(token);
        if (operatorData is null)
        {
            logger.LogWarning("Invalid operator token from IP={Ip}", httpContext.Connection.RemoteIpAddress);
            throw new IonRequestException(new IonProtocolError("NO_OPERATOR_AUTH", "Invalid or expired operator token"));
        }

        OperatorRequestContext.Set(new OperatorRequestContextData
        {
            OperatorId            = operatorData.OperatorId,
            Email                 = operatorData.Email,
            CertificateThumbprint = operatorData.CertificateThumbprint
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
