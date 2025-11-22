namespace Argon.Sfu;

using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.DependencyInjection;

public static class SfuFeature
{
    public static IHostApplicationBuilder AddSelectiveForwardingUnit(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<CallKitOptions>(builder.Configuration.GetSection("CallKit"));
        builder.Services.AddScoped<RoomServiceClient>(x =>
        {
            var options = x.GetRequiredService<IOptions<CallKitOptions>>();
            return new RoomServiceClient(options.Value.Sfu.CommandUrl, options.Value.Sfu.ClientId, options.Value.Sfu.Secret);
        });
        builder.Services.AddScoped<EgressServiceClient>(x =>
        {
            var options = x.GetRequiredService<IOptions<CallKitOptions>>();
            return new EgressServiceClient(options.Value.Sfu.CommandUrl, options.Value.Sfu.ClientId, options.Value.Sfu.Secret);
        });
        builder.Services.AddScoped<IngressServiceClient>(x => {
            var options = x.GetRequiredService<IOptions<CallKitOptions>>();
            return new IngressServiceClient(options.Value.Sfu.CommandUrl, options.Value.Sfu.ClientId, options.Value.Sfu.Secret);
        });
        builder.Services.AddScoped<SipServiceClient>(x => {
            var options = x.GetRequiredService<IOptions<CallKitOptions>>();
            return new SipServiceClient(options.Value.Sfu.CommandUrl, options.Value.Sfu.ClientId, options.Value.Sfu.Secret);
        });
        return builder;
    }
}

public class CallKitOptions
{
    public List<IceCfg>   Ices { get; set; } = new();
    public required SfuInstanceCfg Sfu { get; set; }
}

public enum IceKind
{
    Worldwide,
    GeoLinked
}

public enum IceScenario
{
    Classic,
    Cloudflare
}

public class IceCfg
{
    public required string       Name     { get; set; }
    public required IceKind      Kind     { get; set; }
    public required IceScenario  Scenario { get; set; }
    public required List<string> Urls     { get; set; } = new();


    public string? AppId { get; set; }
    public string? Token { get; set; }
}

public class SfuInstanceCfg
{
    public required string      Region     { get; set; }
    public required string      ClientId   { get; set; }
    public required string      PublicUrl  { get; set; }
    public required string      CommandUrl { get; set; }
    public required string      Secret     { get; set; }
    public required GeoPosition Geo        { get; set; }

    public SfuS3Settings? S3 { get; set; }
}

public record GeoPosition(double ln, double lt);

public class SfuS3Settings
{
    public required string Endpoint  { get; set; }
    public required string Bucket    { get; set; }
    public required string Secret    { get; set; }
    public required string AccessKey { get; set; }
    public required string Region    { get; set; }
}