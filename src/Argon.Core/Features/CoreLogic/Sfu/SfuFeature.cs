namespace Argon.Sfu;

using Argon.Sfu.Services;
using Flurl.Http;
using Flurl.Http.Newtonsoft;
using Newtonsoft.Json.Serialization;

public static class SfuFeature
{
    public static IHostApplicationBuilder AddSelectiveForwardingUnit(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<CallKitOptions>(builder.Configuration.GetSection("CallKit"));

        builder.Services.AddKeyedScoped<IFlurlClient>(IArgonSelectiveForwardingUnit.CHANNEL_KEY, (provider, _) =>
        {
            var opt = provider.GetRequiredService<IOptions<CallKitOptions>>();

            var client = new FlurlClient(opt.Value.Sfu.CommandUrl);

            client.Settings.JsonSerializer = new NewtonsoftJsonSerializer(new JsonSerializerSettings()
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });

            return client;
        });
        builder.Services.AddScoped<TwirpClient>();
        builder.Services.AddScoped<TwirlRoomServiceClient>();
        builder.Services.AddScoped<TwirlEgressClient>();

        builder.Services.AddTransient<IArgonSelectiveForwardingUnit, ArgonSelectiveForwardingUnit>();
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