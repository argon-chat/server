namespace Argon.Sfu;

using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using LiveKit.Proto;

public static class SfuFeature
{
    public static IHostApplicationBuilder AddSelectiveForwardingUnit(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<SfuFeatureSettings>(builder.Configuration.GetSection("sfu"));

        builder.Services.AddKeyedScoped<GrpcChannel>(IArgonSelectiveForwardingUnit.GRPC_CHANNEL_KEY, (provider, _) =>
        {
            var opt = provider.GetRequiredService<IOptions<SfuFeatureSettings>>();

            var innerHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            var grpcWebHandler = new GrpcWebHandler(GrpcWebMode.GrpcWebText, innerHandler)
            {
                HttpVersion = new Version(1,1)
            };

            return GrpcChannel.ForAddress(opt.Value.Url, new GrpcChannelOptions
            {
                HttpHandler = grpcWebHandler,
                Credentials = ChannelCredentials.Insecure 
            });
        });

        builder.Services.AddScoped<Egress.EgressClient>(provider
            => new Egress.EgressClient(provider.GetRequiredKeyedService<GrpcChannel>(IArgonSelectiveForwardingUnit.GRPC_CHANNEL_KEY)));
        builder.Services.AddScoped<Ingress.IngressClient>(provider
            => new Ingress.IngressClient(provider.GetRequiredKeyedService<GrpcChannel>(IArgonSelectiveForwardingUnit.GRPC_CHANNEL_KEY)));
        builder.Services.AddScoped<RoomService.RoomServiceClient>(provider
            => new RoomService.RoomServiceClient(provider.GetRequiredKeyedService<GrpcChannel>(IArgonSelectiveForwardingUnit.GRPC_CHANNEL_KEY)));
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