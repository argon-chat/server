namespace Argon.Sfu;

using Flurl.Http;
using Microsoft.Extensions.DependencyInjection;
using Flurl.Http.Newtonsoft;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

public static class SfuFeature
{
    public const string HttpClientKey = "sfu_flurl_http_client";

    public static IHostApplicationBuilder AddSelectiveForwardingUnit(this IHostApplicationBuilder builder)
    {
        builder.Services.AddTransient<IArgonSelectiveForwardingUnit, ArgonSelectiveForwardingUnit>();
        builder.Services.Configure<SfuFeatureSettings>(builder.Configuration.GetSection("sfu"));
        builder.Services.AddKeyedScoped<IFlurlClient, FlurlClient>(HttpClientKey, (provider, o) =>
        {
            var client = new FlurlClient(provider.GetRequiredService<IOptions<SfuFeatureSettings>>().Value.Url);
            client.Settings.JsonSerializer = new NewtonsoftJsonSerializer(new JsonSerializerSettings()
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });
            return client;
        });

        return builder;
    }
}

public class SfuFeatureSettings
{
    public required string Url { get; set; }
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
}