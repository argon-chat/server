namespace Argon.Features.Web;

public static class KestrelFeature
{
    public static void ConfigureDefaultKestrel(this WebApplicationBuilder builder)
        => builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.KeepAliveTimeout                  = TimeSpan.FromSeconds(400);
            options.AddServerHeader                          = false;
            options.Limits.Http2.MaxStreamsPerConnection     = 100;
            options.Limits.Http2.InitialConnectionWindowSize = 65535;
            options.Limits.Http2.KeepAlivePingDelay          = TimeSpan.FromSeconds(30);
            options.Limits.Http2.KeepAlivePingTimeout        = TimeSpan.FromSeconds(5);
        });
}