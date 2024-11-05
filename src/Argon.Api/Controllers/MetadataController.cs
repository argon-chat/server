namespace Argon.Api.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
public class MetadataController : ControllerBase
{
    [HttpGet("/cfg.json"), AllowAnonymous]
    public ValueTask<HeadRoutingConfig> GetHead() =>
        new(new HeadRoutingConfig(
                $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}",
                "api.argon.gl",
                "argon-f14ic5ia.livekit.cloud",
                [
                    new RegionalNode("cdn-ru1.argon.gl", "ru1"), new RegionalNode("cdn-ru2.argon.gl", "ru1"),
                    new RegionalNode("cdn-as1.argon.gl", "as1"),
                ],
                [new FeatureFlag("dev.window", true), new FeatureFlag("user.allowServerCreation", true)]
            )
        );
}

public record HeadRoutingConfig(
    string version,
    string masterEndpoint,
    string webRtcEndpoint,
    List<RegionalNode> cdnAddresses,
    List<FeatureFlag> features
);

public record RegionalNode(string url, string code);
public record FeatureFlag(string code, bool enabled);
