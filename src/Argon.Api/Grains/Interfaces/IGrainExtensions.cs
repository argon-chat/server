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

    public static string? GetUserIp(this Grain grain)
        => RequestContext.Get("$caller_user_ip") as string;
    public static string? GetUserRegion(this Grain grain)
        => RequestContext.Get("$caller_country") as string;

    public static string GetUserMachineId(this Grain grain)
    {
        var result = RequestContext.Get("$caller_machine_id") as string;
        if (string.IsNullOrEmpty(result))
            throw new NotAuthorizedCallException();
        return result;
    }

    public static Guid? GetUserId(this IIncomingGrainCallContext ctx)
    {
        var result = RequestContext.Get("$caller_user_id");
        if (result is Guid g)
            return g;
        return null;
    }

    public static string? GetUserIp(this IIncomingGrainCallContext ctx)
        => RequestContext.Get("$caller_user_ip") as string;
    public static string? GetUserRegion(this IIncomingGrainCallContext ctx)
        => RequestContext.Get("$caller_country") as string;

    public static Guid? GetReentrancyId(this IIncomingGrainCallContext ctx)
    {
        var result = RequestContext.ReentrancyId;
        if (result == Guid.Empty)
            return null;
        return result;
    }

    // RequestContext.AllowCallChainReentrancy()
    public static void SetUserId(this IIonService that, Guid userId)
        => RequestContext.Set("$caller_user_id", userId);
    public static void SetUserIp(this IIonService that, string ip)
        => RequestContext.Set("$caller_user_ip", ip);
    public static void SetUserMachineId(this IIonService that, string machineId)
        => RequestContext.Set("$caller_machine_id", machineId);
    public static void SetUserSessionId(this IIonService that, Guid sessionId)
        => RequestContext.Set("$caller_session_id", sessionId);
    public static void SetUserCountry(this IIonService that, string Country)
        => RequestContext.Set("$caller_country", Country);

    public static void SetUserIp(this RequestContext.ReentrancySection that, string userIp)
        => RequestContext.Set("$caller_user_ip", userIp);
    public static void SetUserId(this RequestContext.ReentrancySection that, Guid userId)
        => RequestContext.Set("$caller_user_id", userId);
    public static void SetUserMachineId(this RequestContext.ReentrancySection that, string machineId)
        => RequestContext.Set("$caller_machine_id", machineId);
    public static void SetUserSessionId(this RequestContext.ReentrancySection that, Guid sessionId)
        => RequestContext.Set("$caller_session_id", sessionId);
    public static void SetUserCountry(this RequestContext.ReentrancySection that, string Country)
        => RequestContext.Set("$caller_country", Country);

    //  RequestContext.ReentrancySection
}

public class NotAuthorizedCallException : Exception;