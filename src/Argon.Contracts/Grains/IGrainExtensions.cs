namespace Argon.Grains.Interfaces;

public static class IGrainExtensions
{
    public static Guid GetUserId(this Grain grain)
    {
        var result = RequestContext.Get("$caller_user_id");
        if (result is null)
            throw new NotAuthorizedCallException();
        if (result is Guid g)
            return g;
        throw new NotAuthorizedCallException();
    }

    public static Guid GetUserMachineId(this Grain grain)
    {
        var result = RequestContext.Get("$caller_machine_id");
        if (result is null)
            throw new NotAuthorizedCallException();
        if (result is Guid g)
            return g;
        throw new NotAuthorizedCallException();
    }

    public static Guid? GetUserId(this IIncomingGrainCallContext ctx)
    {
        var result = RequestContext.Get("$caller_user_id");
        if (result is Guid g)
            return g;
        return null;
    }

    public static Guid? GetReentrancyId(this IIncomingGrainCallContext ctx)
    {
        var result = RequestContext.ReentrancyId;
        if (result == Guid.Empty)
            return null;
        return result;
    }

    // RequestContext.AllowCallChainReentrancy()
    public static void SetUserId(this IArgonService that, Guid userId)
        => RequestContext.Set("$caller_user_id", userId);
    public static void SetUserMachineId(this IArgonService that, Guid machineId)
        => RequestContext.Set("$caller_machine_id", machineId);
    public static void SetUserSessionId(this IArgonService that, Guid sessionId)
        => RequestContext.Set("$caller_session_id", sessionId);
    public static void SetUserCountry(this IArgonService that, string Country)
        => RequestContext.Set("$caller_country", Country);
}

public class NotAuthorizedCallException : Exception;