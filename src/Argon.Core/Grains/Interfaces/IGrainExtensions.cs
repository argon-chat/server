namespace Argon.Grains.Interfaces;

using Argon.Features.Auth;
using Microsoft.AspNetCore.SignalR;

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

    /// <summary>The application id (<c>ner</c>) the calling request carried, or null when it carried none.</summary>
    public static string? GetUserAppId(this Grain grain)
        => RequestContext.Get(CallerContext.AppIdKey) as string;

    /// <summary>The city the edge placed the caller in, or null.</summary>
    public static string? GetUserCity(this Grain grain)
        => RequestContext.Get(CallerContext.CityKey) as string;

    /// <summary>What the calling client said about itself. Never null; unknown when nothing was carried.</summary>
    public static ClientDescriptor GetUserClient(this Grain grain)
        => ClientDescriptor.FromTransport(RequestContext.Get(CallerContext.ClientKey) as string);

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
    public static void SetUserId(this Hub that, Guid userId)
        => RequestContext.Set("$caller_user_id", userId);
    public static void SetUserIp(this IIonService that, string ip)
        => RequestContext.Set("$caller_user_ip", ip);
    public static void SetUserMachineId(this IIonService that, string machineId)
        => RequestContext.Set("$caller_machine_id", machineId);
    public static void SetUserMachineId(this Hub that, string machineId)
        => RequestContext.Set("$caller_machine_id", machineId);
    public static void SetUserSessionId(this IIonService that, Guid sessionId)
        => RequestContext.Set("$caller_session_id", sessionId);
    public static void SetUserSessionId(this Hub that, Guid sessionId)
        => RequestContext.Set("$caller_session_id", sessionId);
    public static void SetUserCountry(this IIonService that, string Country)
        => RequestContext.Set("$caller_country", Country);

    /// <summary>
    /// Marks the calling request as coming from a machine whose hardware key just proved itself.
    /// </summary>
    /// <remarks>
    /// Read by the token issuer inside the authorization grain, which puts the thumbprint on the
    /// refresh token as <c>cnf</c>. Set only after <c>DeviceProofVerifier</c> accepted a proof — this
    /// is the one caller value on this list that is a security fact rather than a description.
    /// </remarks>
    public static void SetUserDeviceThumbprint(this IIonService that, string? thumbprint)
        => CallerContext.SetOptional(CallerContext.DeviceThumbprintKey, thumbprint);
    public static void SetUserAppId(this IIonService that, string? appId)
        => CallerContext.SetOptional(CallerContext.AppIdKey, appId);
    public static void SetUserClient(this IIonService that, ClientDescriptor client)
        => CallerContext.SetOptional(CallerContext.ClientKey, client.IsEmpty ? null : client.ToTransport());
    public static void SetUserCity(this IIonService that, string? city)
        => CallerContext.SetOptional(CallerContext.CityKey, city);

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
    public static void SetUserAppId(this RequestContext.ReentrancySection that, string? appId)
        => CallerContext.SetOptional(CallerContext.AppIdKey, appId);
    public static void SetUserClient(this RequestContext.ReentrancySection that, ClientDescriptor client)
        => CallerContext.SetOptional(CallerContext.ClientKey, client.IsEmpty ? null : client.ToTransport());
    public static void SetUserCity(this RequestContext.ReentrancySection that, string? city)
        => CallerContext.SetOptional(CallerContext.CityKey, city);

    //  RequestContext.ReentrancySection
}

/// <summary>
/// The keys under which the Ion layer describes a caller to the grains, and the one reader that is
/// not a grain.
/// </summary>
/// <remarks>
/// <c>ArgonAuthorizationService</c> mints tokens from inside a grain call but is a plain service, so
/// it cannot use the <c>Grain</c> extensions above; <see cref="DeviceThumbprint"/> is the same read
/// without the receiver.
/// </remarks>
public static class CallerContext
{
    public const string AppIdKey            = "$caller_app_id";
    public const string ClientKey           = "$caller_client";
    public const string CityKey             = "$caller_city";
    public const string DeviceThumbprintKey = "$caller_device_thumbprint";

    /// <summary>The verified hardware-key thumbprint of the calling machine, or null when none was proven.</summary>
    public static string? DeviceThumbprint => RequestContext.Get(DeviceThumbprintKey) as string;

    /// <summary>Sets a value, or clears the key when there is nothing to say — a null in the context is a stale answer waiting to be read.</summary>
    public static void SetOptional(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            RequestContext.Remove(key);
        else
            RequestContext.Set(key, value);
    }
}

public class NotAuthorizedCallException : Exception;
