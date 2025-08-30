namespace Argon.Sfu;

using Argon.Sfu.Services;
using Flurl.Http;
using Flurl.Http.Newtonsoft;
using Newtonsoft.Json.Serialization;

public static class SfuFeature
{
    public static IHostApplicationBuilder AddSelectiveForwardingUnit(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<SfuFeatureSettings>(builder.Configuration.GetSection("sfu"));

        builder.Services.AddKeyedScoped<IFlurlClient>(IArgonSelectiveForwardingUnit.CHANNEL_KEY, (provider, _) =>
        {
            var opt = provider.GetRequiredService<IOptions<SfuFeatureSettings>>();

            var client = new FlurlClient(opt.Value.Url);

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



public class SfuFeatureSettings
{
    public required string        Url          { get; set; }
    public required string        ClientId     { get; set; }
    public required string        ClientSecret { get; set; }
    public          SfuS3Settings S3           { get; set; }
}

public class SfuS3Settings
{
    public string Endpoint  { get; set; }
    public string Bucket    { get; set; }
    public string Secret    { get; set; }
    public string AccessKey { get; set; }
    public string Region    { get; set; }
}