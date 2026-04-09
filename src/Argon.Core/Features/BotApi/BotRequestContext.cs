namespace Argon.Features.BotApi;

/// <summary>
/// Provides the bot identity context for the current request.
/// Used by Bot API endpoints to resolve the acting bot user and propagate
/// identity into Orleans grain calls via RequestContext.
/// </summary>
public static class BotRequestContext
{
    public static Guid GetBotAppId(this HttpContext ctx)
        => Guid.Parse(ctx.User.FindFirst("bot_id")!.Value);

    public static Guid GetBotAsUserId(this HttpContext ctx)
        => Guid.Parse(ctx.User.FindFirst("bot_as_user_id")!.Value);

    public static Guid GetBotTeamId(this HttpContext ctx)
        => Guid.Parse(ctx.User.FindFirst("team_id")!.Value);

    public static string GetBotName(this HttpContext ctx)
        => ctx.User.FindFirst("bot_name")!.Value;

    public static bool GetBotIsVerified(this HttpContext ctx)
        => bool.TryParse(ctx.User.FindFirst("is_verified")?.Value, out var v) && v;

    /// <summary>
    /// Sets Orleans RequestContext so that grains see the bot as a regular user.
    /// </summary>
    public static void PropagateToOrleans(this HttpContext ctx)
    {
        var section = RequestContext.AllowCallChainReentrancy();
        section.SetUserId(ctx.GetBotAsUserId());
        section.SetUserIp(ctx.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0");
        section.SetUserMachineId($"bot:{ctx.GetBotAppId()}");
    }
}

/// <summary>
/// Endpoint filter that propagates bot identity into Orleans RequestContext
/// before every Bot API handler invocation.
/// </summary>
public sealed class BotOrleansPropagationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.PropagateToOrleans();
        return await next(context);
    }
}

/// <summary>
/// Endpoint filter that verifies the bot is a member of the requested space.
/// Looks for a SpaceId property in request body arguments or a spaceId query parameter.
/// </summary>
public sealed class BotSpaceMembershipFilter(IGrainFactory grains) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var botUserId = context.HttpContext.GetBotAsUserId();

        Guid? spaceId = null;

        if (context.HttpContext.Request.Query.TryGetValue("spaceId", out var qs) && Guid.TryParse(qs, out var qsId))
            spaceId = qsId;

        if (!spaceId.HasValue)
        {
            foreach (var arg in context.Arguments)
            {
                if (arg is null) continue;
                var prop = arg.GetType().GetProperty("SpaceId");
                if (prop?.PropertyType == typeof(Guid) && prop.GetValue(arg) is Guid bodyId)
                {
                    spaceId = bodyId;
                    break;
                }
            }
        }

        if (!spaceId.HasValue)
            return Results.BadRequest(new { error = "missing_space_id", message = "spaceId is required." });

        try
        {
            var members = await grains.GetGrain<ISpaceGrain>(spaceId.Value).GetMembers();

            if (members.All(m => m.member.userId != botUserId))
                return Results.Json(
                    new { error = "not_a_member", message = "Bot is not a member of this space." },
                    statusCode: StatusCodes.Status403Forbidden);
        }
        catch (Exception)
        {
            return Results.Json(
                new { error = "not_a_member", message = "Bot is not a member of this space." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}
