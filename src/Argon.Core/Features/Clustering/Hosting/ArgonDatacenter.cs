namespace Argon.Features.Clustering;

/// <summary>
/// The datacenter this process belongs to. Feeds the Orleans <c>ServiceId</c> and is injected as a
/// keyed <c>string</c> under "dc" by several cluster components.
/// </summary>
/// <remarks>
/// Previously discovered by <c>RegionalUnitApp.CreateBuilder</c>, which existed to run a throwaway
/// web host and ask Consul which datacenter it was in. That path had been commented out for a long
/// time and the fallback — read one environment variable — was all that ran.
/// </remarks>
public static class ArgonDatacenter
{
    public const string ServiceKey = "dc";
    public const string Default    = "ru-3";

    public static string Current
        => Environment.GetEnvironmentVariable("ARGON_REGION_DC") ?? Default;

    public static WebApplicationBuilder AddArgonDatacenter(this WebApplicationBuilder builder)
    {
        builder.Services.AddKeyedSingleton(ServiceKey, Current);
        return builder;
    }
}
