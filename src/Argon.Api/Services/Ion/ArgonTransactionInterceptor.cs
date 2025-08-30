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
        section.SetUserMachineId(ctx.MachineId);
        section.SetUserSessionId(ctx.SessionId);
        return next(context, ct);
    }

}

public sealed class ArgonTransactionInterceptor(TokenAuthorization validationParameters, ILogger<ArgonTransactionInterceptor> logger)
    : IIonInterceptor
{
    public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        var headers = context.RequestItems;

        var allowAnonymous = context.MethodName.GetCustomAttribute<AllowAnonymousAttribute>() != null;

        Guid? user = null;
        if (!allowAnonymous)
        {
            user = await Authorize(headers);
        }


        if (!allowAnonymous && user is null)
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));

        try
        {
            var data = new ArgonRequestContextData
            {
                Ip         = headers.TryGetValue("CF-Connecting-IP", out var ip) ? ip : "unknown",
                Region     = headers.TryGetValue("CF-IPCountry", out var region) ? region : "unknown",
                Ray        = headers.TryGetValue("CF-Ray", out var ray) ? ray : Guid.NewGuid().ToString(),
                ClientName = headers.TryGetValue("User-Agent", out var ua) ? ua : "unknown",
                HostName   = headers.TryGetValue("X-Host-Name", out var host) ? host : string.Empty,
                SessionId  = ResolveSessionId(headers),
                MachineId  = ResolveMachineId(headers),
                AppId      = ResolveAppId(headers),
                UserId     = user,
                Scope      = context.AsyncServiceScope
            };

            ArgonRequestContext.Set(data);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Trying access to argon api, but incorrect configuration client");
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));
        }

        await next(context, ct);
    }

    private async Task<Guid?> Authorize(IDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Authorization", out var auth) || string.IsNullOrWhiteSpace(auth))
            throw new UnauthorizedAccessException("Authorization header missing");

        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Authorization header must be Bearer");

        var token = auth["Bearer ".Length..].Trim();

        var authResult = await validationParameters.AuthorizeByToken(token);

        if (authResult.IsSuccess)
            return authResult.Value.id;
        return null;
    }

    private static Guid ResolveSessionId(IDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Sec-Ref", out var secRef) && !headers.TryGetValue("X-Sec-Ref", out secRef))
            throw new InvalidOperationException("SessionId is not defined");
        if (Guid.TryParse(secRef, out var sid))
            return sid;
        throw new InvalidOperationException("SessionId invalid");
    }

    private static string ResolveAppId(IDictionary<string, string> headers)
    {
        if (headers.TryGetValue("Sec-Ner", out var secNer) || headers.TryGetValue("X-Sec-Ner", out secNer))
            return secNer;

        throw new InvalidOperationException("SessionId is not defined");
    }

    private static string ResolveMachineId(IDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Sec-Carry", out var carry) && !headers.TryGetValue("X-Sec-Carry", out carry))
            throw new InvalidOperationException("MachineId is not defined");
        if (!string.IsNullOrEmpty(carry))
            return carry;
        throw new InvalidOperationException("MachineId invalid");
    }
}