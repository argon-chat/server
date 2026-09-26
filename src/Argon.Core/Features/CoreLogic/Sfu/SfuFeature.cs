namespace Argon.Sfu;

using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class SfuFeature
{
    public static IHostApplicationBuilder AddSelectiveForwardingUnit(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddScoped<RoomServiceClient>(x =>
        {
            var options = x.GetRequiredService<IOptions<CallKitOptions>>();
            // Bounded: a grain turn that talks to the SFU must not hang on an unreachable one.
            var http = new HttpClient { Timeout = options.Value.Sfu.CommandTimeout };
            return new RoomServiceClient(options.Value.Sfu.CommandUrl, options.Value.Sfu.ClientId, options.Value.Sfu.Secret, http);
        });
        builder.Services.TryAddScoped<EgressServiceClient>(x =>
        {
            var options = x.GetRequiredService<IOptions<CallKitOptions>>();
            return new EgressServiceClient(options.Value.Sfu.CommandUrl, options.Value.Sfu.ClientId, options.Value.Sfu.Secret);
        });
        builder.Services.TryAddScoped<IngressServiceClient>(x =>
        {
            var options = x.GetRequiredService<IOptions<CallKitOptions>>();
            return new IngressServiceClient(options.Value.Sfu.CommandUrl, options.Value.Sfu.ClientId, options.Value.Sfu.Secret);
        });
        builder.Services.TryAddScoped<WebhookReceiver>(x =>
        {
            var options = x.GetRequiredService<IOptions<CallKitOptions>>();
            return new WebhookReceiver(options.Value.Sfu.ClientId, options.Value.Sfu.Secret);
        });
        builder.Services.TryAddScoped<ISfuAuthScope, SfuAuthScope>();
        return builder;
    }
}

public interface ISfuAuthScope
{
    string GenerateToken(string identity, string roomId, string displayName, VideoGrants grants, TimeSpan ttl);
}

public class SfuAuthScope(IOptions<CallKitOptions> options) : ISfuAuthScope
{
    public string GenerateToken(string identity, string roomId, string displayName, VideoGrants grants, TimeSpan ttl)
    {
        var token = new AccessToken(options.Value.Sfu.ClientId, options.Value.Sfu.Secret)
           .WithIdentity(identity)
           .WithName(displayName)
           .WithGrants(new VideoGrants
            {
                RoomJoin             = true,
                CanPublish           = true,
                CanSubscribe         = true,
                RoomCreate           = true,
                CanPublishData       = true,
                CanUpdateOwnMetadata = true,
                CanSubscribeMetrics  = true,
                Room                 = roomId
            })
           .WithTtl(ttl);
        return token.ToJwt();
    }
}

public class CallKitOptions
{
    public          List<IceCfg>   Ices { get; set; } = new();
    public required SfuInstanceCfg Sfu  { get; set; }
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
    public required string      Secret          { get; set; }
    public required GeoPosition Geo             { get; set; }
    public          string      AudioIngressUrl { get; set; } = "";

    public SfuS3Settings? S3 { get; set; }

    /// <summary>How long one room-service call may take before it counts as failed.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>What the LiveKit build behind <see cref="CommandUrl"/> supports beyond upstream: <see cref="ForwardCapability"/>, <see cref="MoveCapability"/>.</summary>
    public List<string> Capabilities { get; set; } = [];

    public const string ForwardCapability = "Forward";
    public const string MoveCapability    = "Move";

    public bool Has(string capability)
        => Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);
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