namespace Argon.Api.Controllers;

using Argon.Features;
using Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
public class MetadataController : ControllerBase
{
    [HttpGet("/cfg.json"), AllowAnonymous]
    public ValueTask<HeadRoutingConfig> GetHead()
        => new(new HeadRoutingConfig($"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}",
            "api.argon.gl", "argon-f14ic5ia.livekit.cloud", [
                new FeatureFlag("dev.window", true),
                new FeatureFlag("user.allowServerCreation", true)
            ], this.HttpContext.GetRegion()));
}

public record HeadRoutingConfig(
    string version,
    string masterEndpoint,
    string webRtcEndpoint,
    List<FeatureFlag> features,
    string currentRegionCode);

public record FeatureFlag(string code, bool enabled);