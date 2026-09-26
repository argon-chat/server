namespace ArgonSharedLogicTest;

using System.Diagnostics;
using Argon.Features.Logging;
using Sentry;

/// <summary>
/// An incoming webhook's URL and a bot's path-token URL carry the credential in the path, so every
/// path that leaves the process for a log, a trace or Sentry goes through <see cref="SecretPaths"/>.
/// </summary>
[TestFixture]
public class SecretPathsTests
{
    private const string WebhookId = "6f1c1f5e-3b7d-4c55-9a55-1b2c3d4e5f60";
    private const string Token     = "Qx3v-8kP_2mN7rT1yU4iO9pA6sD5fG0hJ3kL2zX1cV8";
    private const string BotToken  = "0123456789abcdef0123456789ABCDEF:c2VjcmV0LXRva2VuLXZhbHVlLWhlcmU";

    [TestCase("/api/webhooks/" + WebhookId + "/" + Token, "/api/webhooks/" + WebhookId + "/***")]
    [TestCase("https://api.argon.gl/api/webhooks/" + WebhookId + "/" + Token + "?wait=true",
        "https://api.argon.gl/api/webhooks/" + WebhookId + "/***?wait=true")]
    [TestCase("POST /API/Webhooks/" + WebhookId + "/" + Token, "POST /API/Webhooks/" + WebhookId + "/***")]
    [TestCase("/api/webhooks/not-a-guid/whatever", "/api/webhooks/not-a-guid/***")]
    [TestCase("/api/bot/" + BotToken + "/IMessages/v1/Send", "/api/bot/***/IMessages/v1/Send")]
    [TestCase("/api/bot/0123456789abcdef0123456789abcdef%3Asecret/IMessages/v1/Send", "/api/bot/***/IMessages/v1/Send")]
    [TestCase("HTTP POST /api/webhooks/" + WebhookId + "/" + Token + " responded 500", "HTTP POST /api/webhooks/" + WebhookId + "/*** responded 500")]
    public void A_secret_in_the_path_is_masked(string value, string expected)
        => Assert.That(SecretPaths.Redact(value), Is.EqualTo(expected));

    [TestCase("/api/bot/IMessages/v1/Send")]
    [TestCase("/api/bot/openapi.json")]
    [TestCase("/api/invite/abc-def-ghi")]
    [TestCase("/api/webhooks/{webhookId:guid}/{token}")]
    [TestCase("/ion/ChannelInteraction/SendMessage")]
    [TestCase("")]
    [TestCase(null)]
    public void A_path_without_a_secret_is_left_alone(string? value)
        => Assert.That(SecretPaths.Redact(value), Is.EqualTo(value));

    [Test]
    public void The_path_tags_of_a_server_span_are_masked()
    {
        using var activity = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn");
        activity.SetTag("url.path", $"/api/webhooks/{WebhookId}/{Token}");
        activity.SetTag("url.full", $"https://api.argon.gl/api/bot/{BotToken}/IMessages/v1/Send");
        activity.SetTag("http.request.method", "POST");
        activity.DisplayName = $"POST /api/webhooks/{WebhookId}/{Token}";

        SecretPaths.Redact(activity);

        Assert.Multiple(() =>
        {
            Assert.That(activity.GetTagItem("url.path"), Is.EqualTo($"/api/webhooks/{WebhookId}/***"));
            Assert.That(activity.GetTagItem("url.full"), Is.EqualTo("https://api.argon.gl/api/bot/***/IMessages/v1/Send"));
            Assert.That(activity.GetTagItem("http.request.method"), Is.EqualTo("POST"));
            Assert.That(activity.DisplayName, Does.Not.Contain(Token));
        });
    }

    [Test]
    public void A_sentry_event_loses_the_token_from_its_url_transaction_and_message()
    {
        var evt = new SentryEvent
        {
            TransactionName = $"POST /api/webhooks/{WebhookId}/{Token}",
            Message         = new SentryMessage { Formatted = $"failed POST /api/webhooks/{WebhookId}/{Token}" }
        };
        evt.Request.Url = $"https://api.argon.gl/api/webhooks/{WebhookId}/{Token}";

        var sent = SecretPaths.Redact(evt);

        Assert.Multiple(() =>
        {
            Assert.That(sent.Request.Url, Is.EqualTo($"https://api.argon.gl/api/webhooks/{WebhookId}/***"));
            Assert.That(sent.TransactionName, Does.Not.Contain(Token));
            Assert.That(sent.Message!.Formatted, Does.Not.Contain(Token));
        });
    }

    [Test]
    public void The_request_scope_path_on_a_log_line_is_masked()
    {
        var logEvent = new Serilog.Events.LogEvent(DateTimeOffset.UtcNow, Serilog.Events.LogEventLevel.Error, null,
            new Serilog.Parsing.MessageTemplateParser().Parse("request failed"),
            [
                new Serilog.Events.LogEventProperty("RequestPath", new Serilog.Events.ScalarValue($"/api/bot/{BotToken}/IMessages/v1/Send")),
                new Serilog.Events.LogEventProperty("RequestId", new Serilog.Events.ScalarValue("0HN1"))
            ]);

        new SecretPathEnricher().Enrich(logEvent, null!);

        Assert.Multiple(() =>
        {
            Assert.That(((Serilog.Events.ScalarValue)logEvent.Properties["RequestPath"]).Value, Is.EqualTo("/api/bot/***/IMessages/v1/Send"));
            Assert.That(((Serilog.Events.ScalarValue)logEvent.Properties["RequestId"]).Value, Is.EqualTo("0HN1"));
        });
    }

    [Test]
    public void A_breadcrumb_with_a_token_is_replaced_by_a_masked_one()
    {
        var crumb = new Breadcrumb($"POST /api/bot/{BotToken}/IMessages/v1/Send", "http",
            new Dictionary<string, string> { ["url"] = $"/api/webhooks/{WebhookId}/{Token}", ["method"] = "POST" }, "http");

        var masked = SecretPaths.Redact(crumb);
        var plain  = new Breadcrumb("nothing secret", "default");

        Assert.Multiple(() =>
        {
            Assert.That(masked.Message, Is.EqualTo("POST /api/bot/***/IMessages/v1/Send"));
            Assert.That(masked.Data!["url"], Is.EqualTo($"/api/webhooks/{WebhookId}/***"));
            Assert.That(masked.Data["method"], Is.EqualTo("POST"));
            Assert.That(masked.Category, Is.EqualTo("http"));
            Assert.That(SecretPaths.Redact(plain), Is.SameAs(plain));
        });
    }
}
