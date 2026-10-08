namespace Argon.Grains.Interfaces;

/// <summary>
/// The keys under which the Ion layer describes a caller to the grains, and the readers that are
/// not a grain.
/// </summary>
/// <remarks>
/// <c>ArgonAuthorizationService</c> mints tokens from inside a grain call but is a plain service, so
/// it cannot use the <c>Grain</c> extensions; <see cref="DeviceThumbprint"/> is the same read
/// without the receiver.
/// </remarks>
public static class CallerContext
{
    public const string UserIdKey           = "$caller_user_id";
    public const string UserIpKey           = "$caller_user_ip";
    public const string CountryKey          = "$caller_country";
    public const string MachineIdKey        = "$caller_machine_id";
    public const string SessionIdKey        = "$caller_session_id";
    public const string AppIdKey            = "$caller_app_id";
    public const string ClientKey           = "$caller_client";
    public const string CityKey             = "$caller_city";
    public const string DeviceThumbprintKey = "$caller_device_thumbprint";

    /// <summary>The verified hardware-key thumbprint of the calling machine, or null when none was proven.</summary>
    public static string? DeviceThumbprint => RequestContext.Get(DeviceThumbprintKey) as string;

    /// <summary>The caller's address, as <c>GetUserIp</c> reads it, for an entry point that is not an Ion call.</summary>
    public static void SetIp(string ip)
        => RequestContext.Set(UserIpKey, ip);

    /// <summary>Sets a value, or clears the key when there is nothing to say — a null in the context is a stale answer waiting to be read.</summary>
    public static void SetOptional(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            RequestContext.Remove(key);
        else
            RequestContext.Set(key, value);
    }
}

public static class GrainCallContextExtensions
{
    public static Guid? GetUserId(this IIncomingGrainCallContext ctx)
    {
        var result = RequestContext.Get(CallerContext.UserIdKey);
        if (result is Guid g)
            return g;
        return null;
    }

    public static string? GetUserIp(this IIncomingGrainCallContext ctx)
        => RequestContext.Get(CallerContext.UserIpKey) as string;
    public static string? GetUserRegion(this IIncomingGrainCallContext ctx)
        => RequestContext.Get(CallerContext.CountryKey) as string;

    public static Guid? GetReentrancyId(this IIncomingGrainCallContext ctx)
    {
        var result = RequestContext.ReentrancyId;
        if (result == Guid.Empty)
            return null;
        return result;
    }
}
