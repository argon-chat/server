namespace Argon.Grains.Interfaces;

using Argon.Features.Auth;
using Microsoft.AspNetCore.SignalR;

public static class IGrainExtensions
{
    public static Guid GetUserId(this Grain grain)
    {
        var result = RequestContext.Get(CallerContext.UserIdKey);
        if (result is null)
            throw new NotAuthorizedCallException();
        if (result is Guid g)
            return g;
        throw new NotAuthorizedCallException();
    }

    public static string? GetUserIp(this Grain grain)
        => RequestContext.Get(CallerContext.UserIpKey) as string;
    public static string? GetUserRegion(this Grain grain)
        => RequestContext.Get(CallerContext.CountryKey) as string;

    public static string GetUserMachineId(this Grain grain)
    {
        var result = RequestContext.Get(CallerContext.MachineIdKey) as string;
        if (string.IsNullOrEmpty(result))
            throw new NotAuthorizedCallException();
        return result;
    }

    /// <summary>True when the call came through the Bot API, which sets the machine id to <c>bot:{appId}</c>.</summary>
    public static bool IsBotCaller(this Grain grain)
        => (RequestContext.Get(CallerContext.MachineIdKey) as string)?.StartsWith("bot:", StringComparison.Ordinal) == true;

    /// <summary>The application id (<c>ner</c>) the calling request carried, or null when it carried none.</summary>
    public static string? GetUserAppId(this Grain grain)
        => RequestContext.Get(CallerContext.AppIdKey) as string;

    /// <summary>The city the edge placed the caller in, or null.</summary>
    public static string? GetUserCity(this Grain grain)
        => RequestContext.Get(CallerContext.CityKey) as string;

    /// <summary>What the calling client said about itself. Never null; unknown when nothing was carried.</summary>
    public static ClientDescriptor GetUserClient(this Grain grain)
        => ClientDescriptor.FromTransport(RequestContext.Get(CallerContext.ClientKey) as string);

    // RequestContext.AllowCallChainReentrancy()
    public static void SetUserId(this IIonService that, Guid userId)
        => RequestContext.Set(CallerContext.UserIdKey, userId);
    public static void SetUserId(this Hub that, Guid userId)
        => RequestContext.Set(CallerContext.UserIdKey, userId);
    public static void SetUserIp(this IIonService that, string ip)
        => RequestContext.Set(CallerContext.UserIpKey, ip);
    public static void SetUserMachineId(this IIonService that, string machineId)
        => RequestContext.Set(CallerContext.MachineIdKey, machineId);
    public static void SetUserMachineId(this Hub that, string machineId)
        => RequestContext.Set(CallerContext.MachineIdKey, machineId);
    public static void SetUserSessionId(this IIonService that, Guid sessionId)
        => RequestContext.Set(CallerContext.SessionIdKey, sessionId);
    public static void SetUserSessionId(this Hub that, Guid sessionId)
        => RequestContext.Set(CallerContext.SessionIdKey, sessionId);
    public static void SetUserCountry(this IIonService that, string Country)
        => RequestContext.Set(CallerContext.CountryKey, Country);

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
        => RequestContext.Set(CallerContext.UserIpKey, userIp);
    public static void SetUserId(this RequestContext.ReentrancySection that, Guid userId)
        => RequestContext.Set(CallerContext.UserIdKey, userId);
    public static void SetUserMachineId(this RequestContext.ReentrancySection that, string machineId)
        => RequestContext.Set(CallerContext.MachineIdKey, machineId);
    public static void SetUserSessionId(this RequestContext.ReentrancySection that, Guid sessionId)
        => RequestContext.Set(CallerContext.SessionIdKey, sessionId);
    public static void SetUserCountry(this RequestContext.ReentrancySection that, string Country)
        => RequestContext.Set(CallerContext.CountryKey, Country);
    public static void SetUserAppId(this RequestContext.ReentrancySection that, string? appId)
        => CallerContext.SetOptional(CallerContext.AppIdKey, appId);
    public static void SetUserClient(this RequestContext.ReentrancySection that, ClientDescriptor client)
        => CallerContext.SetOptional(CallerContext.ClientKey, client.IsEmpty ? null : client.ToTransport());
    public static void SetUserCity(this RequestContext.ReentrancySection that, string? city)
        => CallerContext.SetOptional(CallerContext.CityKey, city);
}

public class NotAuthorizedCallException : Exception;
