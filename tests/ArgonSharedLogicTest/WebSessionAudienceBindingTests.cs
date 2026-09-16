namespace ArgonSharedLogicTest;

using System.Text;
using Argon.Features.WebSession;
using Microsoft.Extensions.Configuration;

/// <summary>
/// That the audiences a deployment writes down are the audiences the validator ends up with.
/// </summary>
/// <remarks>
/// <para>Worth a test of its own because the way this broke was invisible from every direction. The
/// section was keyed by audience — the natural way round — and an audience is a URL. A <c>:</c> in a
/// configuration key is a section separator and nothing escapes it, so <c>https://app.argon.gl</c>
/// never became a key: it became a section <c>https</c> containing a section <c>//app.argon.gl</c>,
/// which binds to a string as null and is skipped.</para>
///
/// <para>What survived was the one spelling with no scheme in it, and that is the one spelling a
/// token can never carry — the authorization endpoint writes the audience as a full origin. So a
/// production deployment listed three audiences, validated clean, reported no error, and refused
/// every exchange it was ever offered.</para>
/// </remarks>
[TestFixture]
public class WebSessionAudienceBindingTests
{
    private static WebSessionOptions Bind(string json)
    {
        var configuration = new ConfigurationBuilder()
           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
           .Build();

        var options = new WebSessionOptions();
        configuration.GetSection(WebSessionOptions.SectionName).Bind(options);

        return options;
    }

    /// <summary>
    /// Every audience an operator wrote reaches the validator, schemes and ports included.
    /// </summary>
    [Test]
    public void Every_configured_audience_survives_binding()
    {
        var options = Bind("""
        {
          "WebSession": {
            "TrustedApplications": {
              "A37E7A1DB06E9610C9C0BD77C61A821B": [
                "https://app.argon.gl",
                "https://app.argon.gl/",
                "https://localhost:5005"
              ]
            }
          }
        }
        """);

        Assert.That(options.TrustedAudiences, Is.EquivalentTo(new[]
        {
            "https://app.argon.gl", "https://app.argon.gl/", "https://localhost:5005"
        }), "an audience written into the configuration did not reach the validator's allowlist");
    }

    /// <summary>
    /// The regression itself: a URL used as a configuration <i>key</i> does not survive, which is why
    /// the application id is the key and the audience is the value.
    /// </summary>
    [Test]
    public void A_url_used_as_a_key_would_not_survive_binding()
    {
        var configuration = new ConfigurationBuilder()
           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
           {
             "Probe": {
               "app.argon.gl":         "kept",
               "https://app.argon.gl": "lost"
             }
           }
           """)))
           .Build();

        var bound = new Dictionary<string, string>();
        configuration.GetSection("Probe").Bind(bound);

        Assert.That(bound.Keys, Is.EquivalentTo(new[] { "app.argon.gl" }),
            "a colon in a configuration key stopped being a section separator, so the audience map "
          + "could be keyed the natural way round again");
    }

    /// <summary>
    /// A port makes a key unusable too, so this is not a scheme-only problem that trimming would fix.
    /// </summary>
    [Test]
    public void A_host_and_port_used_as_a_key_would_not_survive_either()
    {
        var configuration = new ConfigurationBuilder()
           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
           {
             "Probe": { "localhost:5005": "lost" }
           }
           """)))
           .Build();

        var bound = new Dictionary<string, string>();
        configuration.GetSection("Probe").Bind(bound);

        Assert.That(bound, Is.Empty);
    }

    /// <summary>
    /// The audience an exchange arrives on decides the application its session is filed under.
    /// </summary>
    [Test]
    public void An_audience_resolves_to_the_application_it_is_listed_under()
    {
        var options = Bind("""
        {
          "WebSession": {
            "TrustedApplications": {
              "A37E7A1DB06E9610C9C0BD77C61A821B": [ "https://app.argon.gl" ],
              "B48F8B2EC17FA721DAD1CE88D72B932C": [ "https://partner.example" ]
            }
          }
        }
        """);

        Assert.Multiple(() =>
        {
            Assert.That(options.ApplicationFor("https://app.argon.gl"), Is.EqualTo("A37E7A1DB06E9610C9C0BD77C61A821B"));
            Assert.That(options.ApplicationFor("https://partner.example"), Is.EqualTo("B48F8B2EC17FA721DAD1CE88D72B932C"));
            Assert.That(options.ApplicationFor("https://elsewhere.example"), Is.Null,
                "an audience nobody registered resolved to an application anyway");
        });
    }
}
