namespace ArgonSharedLogicTest;

using System.Text.Json;
using Argon.Features.Discovery;

/// <summary>The endpoint names the client's manifest schema reads, as the web defaults write them.</summary>
[TestFixture]
public class DiscoveryManifestTests
{
    [Test]
    public void An_instance_without_webtransport_says_so_with_null()
        => Assert.That(JsonSerializer.Serialize(new ManifestEndpointsDto("https://api", "https://cdn", null), JsonSerializerOptions.Web),
            Is.EqualTo("""{"api":"https://api","cdn":"https://cdn","webTransport":null}"""));

    [Test]
    public void The_webtransport_endpoint_is_published_as_configured()
        => Assert.That(JsonSerializer.Serialize(
                new ManifestEndpointsDto("https://api", "https://cdn", "https://api.argon.gl:4433"), JsonSerializerOptions.Web),
            Does.Contain("\"webTransport\":\"https://api.argon.gl:4433\""));
}
