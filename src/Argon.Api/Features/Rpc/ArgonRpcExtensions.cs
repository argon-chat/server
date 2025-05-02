namespace Argon.Services;

using Features.Jwt;

public static class ArgonRpcExtensions
{
    public static TokenUserData GetUser(this IArgonService argonService)
        => ArgonTransportContext.Current.User;

    public static IGrainFactory GetGrainFactory(this IArgonService appService)
        => appService.GetClusterClient();

    public static IClusterClient GetClusterClient(this IArgonService appService)
        => ArgonTransportContext.Current.GetClusterClient();


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
    public static Guid GetMachineId(this IArgonService argonService)
        => ArgonTransportContext.Current.GetMachineId();
    public static Guid GetSessionId(this IArgonService argonService)
        => ArgonTransportContext.Current.GetSessionId();


    public static Guid TryGetMachineId(this IArgonService argonService)
    {
        try
        {
            return ArgonTransportContext.Current.GetMachineId();
        }
        catch
        {
            return Guid.NewGuid();
        }
    }
}