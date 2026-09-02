namespace ArgonSharedLogicTest;

using Argon.Features.Integrations.Crawler;
using ArgonContracts;
using Microsoft.Extensions.Options;

/// <summary>
/// The link-preview path without a crawler or a host: the crawler's wire format, URL hygiene, what
/// a client-supplied stub is reduced to, and the two small guards around the transport. The card a
/// sender could write is a phishing kit, so the reduction rules are pinned down here in particular.
/// </summary>
[TestFixture]
public class LinkPreviewTests
{
    private const string ReadmeReply = """
        {
          "url": "https://bun.com/",
          "title": "Bun — A fast all-in-one JavaScript runtime",
          "description": "Bundle, install, and run JavaScript & TypeScript",
          "image": "https://bun.com/og.png",
          "imageStored": "https://cdn.argon.gl/xc/abc/og.jpeg",
          "imageS3Key": "xc/abc/og.jpeg",
          "siteName": "Bun",
          "type": "website",
          "favicon": "https://bun.com/favicon.ico",
          "canonical": "https://bun.com/",
          "cachedAt": 1756800000000,
          "fromCache": false
        }
        """;

    // ── Wire ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ParseCrawlReply_ReadsAResult()
    {
        var outcome = CrawlerWire.ParseCrawlReply(ReadmeReply);

        Assert.That(outcome, Is.TypeOf<CrawlOutcome.Success>());
        var result = ((CrawlOutcome.Success)outcome!).Result;
        Assert.Multiple(() =>
        {
            Assert.That(result.Url, Is.EqualTo("https://bun.com/"));
            Assert.That(result.Title, Does.StartWith("Bun"));
            Assert.That(result.ImageStored, Is.EqualTo("https://cdn.argon.gl/xc/abc/og.jpeg"));
            Assert.That(result.SiteName, Is.EqualTo("Bun"));
            Assert.That(result.FromCache, Is.False);
        });
    }

    [Test]
    public void ParseCrawlReply_ReadsAnError()
    {
        var outcome = CrawlerWire.ParseCrawlReply("""{"url":"https://x.test/","error":"Blocked by robots.txt","code":"ROBOTS_BLOCKED","statusCode":403}""");

        Assert.That(outcome, Is.TypeOf<CrawlOutcome.Failure>());
        var error = ((CrawlOutcome.Failure)outcome!).Error;
        Assert.Multiple(() =>
        {
            Assert.That(error.Code, Is.EqualTo("ROBOTS_BLOCKED"));
            Assert.That(error.StatusCode, Is.EqualTo(403));
        });
    }

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("{}")]
    [TestCase("[]")]
    [TestCase("{\"title\":\"no url, no error\"}")]
    public void ParseCrawlReply_RejectsWhatIsNotAReply(string payload)
        => Assert.That(CrawlerWire.ParseCrawlReply(payload), Is.Null);

    [Test]
    public void CrawlRequest_IsTheCrawlersShape()
        => Assert.That(CrawlerWire.CrawlRequest("https://x.test/"), Is.EqualTo("""{"url":"https://x.test/"}"""));

    // ── URLs ────────────────────────────────────────────────────────────────────────────────────

    [TestCase("https://Example.com/Path?q=1#frag", "https://example.com/Path?q=1")]
    [TestCase("http://example.com", "http://example.com/")]
    [TestCase("  https://example.com/a b  ", "https://example.com/a%20b")]
    public void TryNormalize_KeepsSchemeHostPathQuery(string raw, string expected)
    {
        Assert.That(LinkPreviewUrl.TryNormalize(raw, out var normalized), Is.True);
        Assert.That(normalized, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("example.com")]
    [TestCase("ftp://example.com/file")]
    [TestCase("javascript:alert(1)")]
    [TestCase("https://user:secret@example.com/")]
    [TestCase("https://")]
    public void TryNormalize_RefusesWhatIsNotAPage(string? raw)
        => Assert.That(LinkPreviewUrl.TryNormalize(raw, out _), Is.False);

    [Test]
    public void TryNormalize_RefusesOverlongUrls()
        => Assert.That(LinkPreviewUrl.TryNormalize("https://example.com/" + new string('a', LinkPreviewUrl.MaxLength), out _), Is.False);

    [TestCase("see example.com/x", "https://example.com/x/", true)]
    [TestCase("see HTTPS://EXAMPLE.COM/x", "https://example.com/x", true)]
    [TestCase("see https://example.com/a%20b", "https://example.com/a%20b", true)]
    [TestCase("see https://example.com/a b", "https://example.com/a%20b", true)]
    [TestCase("see example.com", "https://other.com/", false)]
    [TestCase("", "https://example.com/", false)]
    public void AppearsIn_MatchesSchemeAside(string text, string url, bool expected)
        => Assert.That(LinkPreviewUrl.AppearsIn(text, url), Is.EqualTo(expected));

    // ── Mapping ─────────────────────────────────────────────────────────────────────────────────

    private static CrawlResult Result(string? title = "Title", string? description = "Desc", string? image = null,
        string? imageStored = null, string? siteName = null, string? canonical = null)
        => new("https://example.com/", title, description, image, imageStored, siteName, "website", null, canonical, false);

    [Test]
    public void ToPreview_PrefersTheReHostedImage()
    {
        var preview = LinkPreviewMapper.ToPreview(
            Result(image: "https://example.com/og.png", imageStored: "https://cdn.argon.gl/xc/1/og.jpeg"),
            "https://example.com/", allowExternalImages: false);

        Assert.That(preview!.imageUrl, Is.EqualTo("https://cdn.argon.gl/xc/1/og.jpeg"));
    }

    [Test]
    public void ToPreview_DropsTheExternalImageUnlessAllowed()
    {
        var result = Result(image: "https://example.com/og.png");

        Assert.Multiple(() =>
        {
            Assert.That(LinkPreviewMapper.ToPreview(result, "https://example.com/", allowExternalImages: false)!.imageUrl, Is.Null);
            Assert.That(LinkPreviewMapper.ToPreview(result, "https://example.com/", allowExternalImages: true)!.imageUrl, Is.EqualTo("https://example.com/og.png"));
        });
    }

    [Test]
    public void ToPreview_IsNothingWhenThePageHasNothingToShow()
        => Assert.That(LinkPreviewMapper.ToPreview(Result(title: null, description: "  ", image: "https://example.com/og.png"), "https://example.com/", false), Is.Null);

    [Test]
    public void ToPreview_FallsBackToTheHostForSiteName()
        => Assert.That(LinkPreviewMapper.ToPreview(Result(), "https://example.com/", false)!.siteName, Is.EqualTo("example.com"));

    [Test]
    public void ToPreview_OmitsACanonicalThatIsTheUrlItself()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LinkPreviewMapper.ToPreview(Result(canonical: "https://example.com/"), "https://example.com/", false)!.canonicalUrl, Is.Null);
            Assert.That(LinkPreviewMapper.ToPreview(Result(canonical: "https://example.com/real"), "https://example.com/", false)!.canonicalUrl, Is.EqualTo("https://example.com/real"));
            Assert.That(LinkPreviewMapper.ToPreview(Result(canonical: "javascript:1"), "https://example.com/", false)!.canonicalUrl, Is.Null);
        });
    }

    [Test]
    public void ToPreview_ClipsLongText()
    {
        var preview = LinkPreviewMapper.ToPreview(Result(description: new string('d', 2000)), "https://example.com/", false);

        Assert.That(preview!.description!.Length, Is.EqualTo(LinkPreviewMapper.MaxDescription));
        Assert.That(preview.description, Does.EndWith("…"));
    }

    // ── Stubs ───────────────────────────────────────────────────────────────────────────────────

    private static MessageEntityLinkPreview Stub(string url, int offset = 0, int length = 0, string? title = null)
        => new(EntityType.LinkPreview, offset, length, 1, url, title, "client says", "client says", "https://evil.test/i.png", "https://evil.test/");

    [Test]
    public void TakeStub_KeepsOnlyTheUrl()
    {
        var text     = "look https://example.com/page";
        var entities = new List<IMessageEntity> { Stub("https://example.com/page", 5, 24, title: "PHISHING") };

        var stub = LinkPreviewEntities.TakeStub(entities, text);

        Assert.That(stub, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stub!.url, Is.EqualTo("https://example.com/page"));
            Assert.That(stub.title, Is.Null);
            Assert.That(stub.description, Is.Null);
            Assert.That(stub.siteName, Is.Null);
            Assert.That(stub.imageUrl, Is.Null);
            Assert.That(stub.canonicalUrl, Is.Null);
            Assert.That(stub.offset, Is.EqualTo(5));
            Assert.That(stub.length, Is.EqualTo(24));
            Assert.That(LinkPreviewEntities.IsPending(stub), Is.True);
            Assert.That(entities, Is.EqualTo(new[] { stub }));
        });
    }

    [Test]
    public void TakeStub_HonoursTheFirstValidOneAndDropsTheRest()
    {
        var text     = "a https://example.com/a b https://example.com/b";
        var bold     = new MessageEntityBold(EntityType.Bold, 0, 1, 1);
        var entities = new List<IMessageEntity>
        {
            bold,
            Stub("not a url"),
            Stub("https://example.com/b"),
            Stub("https://example.com/a"),
        };

        var stub = LinkPreviewEntities.TakeStub(entities, text);

        Assert.That(stub!.url, Is.EqualTo("https://example.com/b"));
        Assert.That(entities, Is.EqualTo(new IMessageEntity[] { bold, stub }));
    }

    [Test]
    public void TakeStub_RefusesALinkTheTextDoesNotCarry()
    {
        var entities = new List<IMessageEntity> { Stub("https://example.com/hidden") };

        Assert.That(LinkPreviewEntities.TakeStub(entities, "nothing to see"), Is.Null);
        Assert.That(entities, Is.Empty);
    }

    [Test]
    public void TakeStub_AcceptsTheSchemelessSpelling()
    {
        var entities = new List<IMessageEntity> { Stub("https://example.com/x") };

        Assert.That(LinkPreviewEntities.TakeStub(entities, "see example.com/x"), Is.Not.Null);
    }

    [Test]
    public void TakeStub_DropsASpanThatDoesNotFitTheText()
    {
        var entities = new List<IMessageEntity> { Stub("https://example.com/", offset: 40, length: 100) };

        var stub = LinkPreviewEntities.TakeStub(entities, "example.com");

        Assert.That((stub!.offset, stub.length), Is.EqualTo((0, 0)));
    }

    [Test]
    public void Fill_TakesEverythingFromThePreview()
    {
        var stub    = Stub("https://example.com/", 3, 10) with { title = null };
        var preview = new LinkPreview("https://example.com/", "T", "D", "S", "https://cdn.argon.gl/i.jpeg", "https://example.com/c");

        var filled = LinkPreviewEntities.Fill(stub, preview);

        Assert.Multiple(() =>
        {
            Assert.That(filled.offset, Is.EqualTo(3));
            Assert.That(filled.length, Is.EqualTo(10));
            Assert.That(filled.title, Is.EqualTo("T"));
            Assert.That(filled.imageUrl, Is.EqualTo("https://cdn.argon.gl/i.jpeg"));
            Assert.That(filled.canonicalUrl, Is.EqualTo("https://example.com/c"));
            Assert.That(LinkPreviewEntities.IsPending(filled), Is.False);
        });
    }

    // ── Guards around the transport ─────────────────────────────────────────────────────────────

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Test]
    public void Circuit_OpensAfterTheThresholdAndProbesAfterTheWait()
    {
        var time    = new FakeTime();
        var circuit = new CrawlerCircuit(3, TimeSpan.FromSeconds(30), time);

        circuit.RecordFailure();
        circuit.RecordFailure();
        Assert.That(circuit.IsOpen, Is.False, "two failures are not yet a verdict");

        circuit.RecordFailure();
        Assert.That(circuit.IsOpen, Is.True);

        time.Now += TimeSpan.FromSeconds(29);
        Assert.That(circuit.IsOpen, Is.True);

        time.Now += TimeSpan.FromSeconds(2);
        Assert.That(circuit.IsOpen, Is.False, "one probe is let through");

        circuit.RecordFailure();
        Assert.That(circuit.IsOpen, Is.True, "a failed probe reopens at once");

        time.Now += TimeSpan.FromSeconds(31);
        circuit.RecordSuccess();
        circuit.RecordFailure();
        Assert.That(circuit.IsOpen, Is.False, "a success starts the count over");
    }

    [Test]
    public void RateLimiter_CountsPerUserPerMinute()
    {
        var time    = new FakeTime();
        var limiter = new LinkPreviewRateLimiter(Options.Create(new CrawlerOptions { PreviewRequestsPerMinute = 2 }), time);
        var alice   = Guid.NewGuid();
        var bob     = Guid.NewGuid();

        Assert.Multiple(() =>
        {
            Assert.That(limiter.TryAcquire(alice), Is.True);
            Assert.That(limiter.TryAcquire(alice), Is.True);
            Assert.That(limiter.TryAcquire(alice), Is.False);
            Assert.That(limiter.TryAcquire(bob), Is.True, "another user has a window of their own");
        });

        time.Now += TimeSpan.FromMinutes(1);
        Assert.That(limiter.TryAcquire(alice), Is.True, "the next minute starts clean");
    }

    [Test]
    public void Options_RefuseASendBudgetLongerThanTheLookup()
    {
        var options = new CrawlerOptions { Timeout = TimeSpan.FromSeconds(1), SendBudget = TimeSpan.FromSeconds(2) };
        var report  = new RecordingReport();

        options.Validate(report);

        Assert.That(report.Errors, Has.Some.Contains(nameof(CrawlerOptions.SendBudget)));
    }

    private sealed class RecordingReport : Argon.Features.Clustering.IFeatureConfigurationReport
    {
        public readonly List<string> Errors = [];

        public string Section       => CrawlerOptions.SectionName;
        public bool   SectionExists => true;

        public TOther Read<TOther>(string section) where TOther : class => throw new NotSupportedException();

        public void Require(bool condition, string setting, string message)
        {
            if (!condition) Errors.Add($"{setting}: {message}");
        }

        public void Invalid(string message) => Errors.Add(message);

        public void Prefer(bool condition, string setting, string message) { }

        public void Required(string? value, string setting)
            => Require(!string.IsNullOrWhiteSpace(value), setting, "is required");

        public void RequireUri(string? value, string setting, params string[] schemes)
            => Require(Uri.TryCreate(value, UriKind.Absolute, out _), setting, "must be a URI");

        public void RequireFile(string? path, string setting)
            => Require(File.Exists(path), setting, "must exist");

        public void RequireRange(int value, int min, int max, string setting)
            => Require(value >= min && value <= max, setting, $"must be within {min}..{max}");

        public void RequireRange(TimeSpan value, TimeSpan min, TimeSpan max, string setting)
            => Require(value >= min && value <= max, setting, $"must be within {min}..{max}");
    }
}
