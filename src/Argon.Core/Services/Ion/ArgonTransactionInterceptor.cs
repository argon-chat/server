namespace Argon.Services.Ion;

using ion.runtime;
using Features.Jwt;
using AllowAnonymousAttribute = ArgonContracts.AllowAnonymousAttribute;

public sealed class ArgonOrleansInterceptor : IIonInterceptor
{
    public Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        var section = RequestContext.AllowCallChainReentrancy();
        var ctx     = ArgonRequestContext.Current;

        if (ctx.UserId is not null)
            section.SetUserId(ctx.UserId!.Value);
        section.SetUserCountry(ctx.Region);
        section.SetUserIp(ctx.Ip);
        if (!string.IsNullOrEmpty(ctx.MachineId))
            section.SetUserMachineId(ctx.MachineId);
        if (ctx.SessionId is not null)
            section.SetUserSessionId(ctx.SessionId.Value);
        return next(context, ct);
    }
}

public sealed class ArgonTransactionInterceptor(TokenAuthorization validationParameters, ILogger<ArgonTransactionInterceptor> logger)
    : IIonInterceptor
{
    public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        var httpAccessor = context.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var httpContext  = httpAccessor.HttpContext;

        if (httpContext is null)
            throw new InvalidOperationException("HttpContext is not available");

        var allowAnonymous             = context.MethodName.GetCustomAttribute<AllowAnonymousAttribute>() != null;
        var doNotRequireSessionContext = context.MethodName.GetCustomAttribute<DoNotRequireSessionContextAttribute>() is not null;

        Guid? user = null;
        if (!allowAnonymous)
        {
            user = await Authorize(httpContext);
        }

        if (!allowAnonymous && user is null)
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));

        if (doNotRequireSessionContext)
            SafeSetRequestContext(context, httpContext, user);
        else
            SetRequestContext(context, httpContext, user);

        await next(context, ct);
    }

    private void SafeSetRequestContext(IIonCallContext context, HttpContext httpContext, Guid? user)
    {
        try
        {
            var data = new ArgonRequestContextData
            {
                Ip         = httpContext.GetIpAddress(),
                Region     = httpContext.GetRegion(),
                Ray        = httpContext.GetRay(),
                ClientName = httpContext.GetClientName(),
                SessionId  = httpContext.TryGetSessionId(out var sessionId) ? sessionId : null,
                MachineId  = httpContext.TryGetMachineId(out var id) ? id : null,
                AppId      = httpContext.TryGetAppId(out var appId) ? appId : null,
                UserId     = user,
                Scope      = context.ServiceProvider
            };

            ArgonRequestContext.Set(data);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Trying access to argon api, but incorrect configuration client");
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));
        }
    }

    private void SetRequestContext(IIonCallContext context, HttpContext httpContext, Guid? user)
    {
        try
        {
            var data = new ArgonRequestContextData
            {
                Ip         = httpContext.GetIpAddress(),
                Region     = httpContext.GetRegion(),
                Ray        = httpContext.GetRay(),
                ClientName = httpContext.GetClientName(),
                SessionId  = httpContext.GetSessionId(),
                MachineId  = httpContext.GetMachineId(),
                AppId      = httpContext.GetAppId(),
                UserId     = user,
                Scope      = context.ServiceProvider
            };

            ArgonRequestContext.Set(data);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Trying access to argon api, but incorrect configuration client");
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));
        }
    }

    private async Task<Guid?> Authorize(HttpContext httpContext)
    {
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var auth) || string.IsNullOrWhiteSpace(auth))
            throw new UnauthorizedAccessException("Authorization header missing");

        if (!auth.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Authorization header must be Bearer");

        var token = auth.ToString()["Bearer ".Length..].Trim();

        var authResult = await validationParameters.AuthorizeByToken(token, httpContext.GetMachineId());

        if (authResult.IsSuccess)
            return authResult.Value.id;
        return null;
    }
}