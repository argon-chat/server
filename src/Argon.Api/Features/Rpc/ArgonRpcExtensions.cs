namespace Argon.Services;

using Features.Jwt;

public static class ArgonRpcExtensions
{
    public static TokenUserData GetUser(this IArgonService argonService)
        => ArgonTransportContext.Current.User;

    public static IGrainFactory GetGrainFactory(this IArgonService appService)
        => throw new NotImplementedException();
    public static IClusterClient GetClusterClient(this IArgonService appService)
        => throw new NotImplementedException();


    public static string GetClientName(this IArgonService argonService)
        => ArgonTransportContext.Current.GetClientName();
    public static string GetHostName(this IArgonService argonService)
        => ArgonTransportContext.Current.GetHostName();
    public static string GetIpAddress(this IArgonService argonService)
        => ArgonTransportContext.Current.GetIpAddress();
    public static string GetRay(this IArgonService argonService)
        => ArgonTransportContext.Current.GetRay();
    public static string GetRegion(this IArgonService argonService)
        => ArgonTransportContext.Current.GetRegion();
}