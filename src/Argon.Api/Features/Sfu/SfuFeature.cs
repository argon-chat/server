namespace Argon.Sfu;

using Flurl.Http;
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
        builder.Services.Configure<SfuFeatureSettings>(config: builder.Configuration.GetSection(key: "sfu"));
        builder.Services.AddKeyedScoped<IFlurlClient, FlurlClient>(serviceKey: HttpClientKey, implementationFactory: (provider, o) =>
        {
            var client = new FlurlClient(baseUrl: provider
                                                  .GetRequiredService<IOptions<SfuFeatureSettings>>().Value.Url);
            client.Settings.JsonSerializer =
                new NewtonsoftJsonSerializer(settings: new JsonSerializerSettings
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
    public required string Url          { get; set; }
    public required string ClientId     { get; set; }
    public required string ClientSecret { get; set; }
}