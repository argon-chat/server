namespace Argon.Api.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
public class MetadataController : ControllerBase
{
    [HttpGet(template: "/cfg.json"), AllowAnonymous]
    public ValueTask<HeadRoutingConfig> GetHead()
        => new(result: new HeadRoutingConfig(
                                             version: $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}",
                                             masterEndpoint: "api.argon.gl",
                                             webRtcEndpoint: "argon-f14ic5ia.livekit.cloud",
                                             cdnAddresses:
                                             [
                                                 new RegionalNode(url: "cdn-ru1.argon.gl", code: "ru1"),
                                                 new RegionalNode(url: "cdn-ru2.argon.gl", code: "ru1"),
                                                 new RegionalNode(url: "cdn-as1.argon.gl", code: "as1")
                                             ], features:
                                             [
                                                 new FeatureFlag(code: "dev.window",               enabled: true),
                                                 new FeatureFlag(code: "user.allowServerCreation", enabled: true)
                                             ]));
}

public record HeadRoutingConfig(
    string             version,
    string             masterEndpoint,
    string             webRtcEndpoint,
    List<RegionalNode> cdnAddresses,
    List<FeatureFlag>  features
);

public record RegionalNode(string url, string code);

public record FeatureFlag(string code, bool enabled);