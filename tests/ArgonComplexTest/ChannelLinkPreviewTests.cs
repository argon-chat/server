namespace ArgonComplexTest.Tests;

using System.Collections.Concurrent;
using System.Text.Json;
using Argon.Features.Integrations.Crawler;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using static ChannelTestKit;

/// <summary>
/// Link cards on channel messages, with a crawler on the other end of the NATS subject that answers
/// the way argon-crawler does.
/// </summary>
/// <remarks>
/// <para>The test host runs no crawler, so without one the only path a message can take is "nobody
/// listening, drop the stub" — which is what <c>MessageTests</c> covers. This fixture puts a responder
/// on the crawler's subject for its lifetime, keyed on the host of the URL asked about: a page that is
/// ready at once, one still being fetched when the send gives up waiting, one that turns out to have
/// nothing on it, and one held until the test has deleted the message it belongs to. Every other URL —
/// another fixture's, running alongside — is answered at once as a page with nothing on it, which is
/// the outcome that fixture already accepts.</para>
///
/// <para>A crawl the send gives up on is not a failure: the crawler keeps going and the message path
/// asks again after the send, with the full lookup time. That second ask is what these tests hold the
/// responder's answer back for.</para>
/// </remarks>
[TestFixture]
public class ChannelLinkPreviewTests : TestBase
{
    private const string ReadyHost   = "ready.argon-crawl.test";
    private const string SlowHost    = "slow.argon-crawl.test";
    private const string EmptyHost   = "empty.argon-crawl.test";
    private const string GatedHost   = "gated.argon-crawl.test";

    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, int>                  asked   = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> gates   = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> replied = new();

    private CancellationTokenSource stop      = null!;
    private Task                    responder = null!;

    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    [OneTimeSetUp]
    public async Task StartCrawler()
    {
        var nats    = Services.GetRequiredService<INatsClient>();
        var subject = $"{Services.GetRequiredService<IOptions<CrawlerOptions>>().Value.SubjectPrefix}.crawl";

        stop      = new CancellationTokenSource();
        responder = Task.Run(async () =>
        {
            await foreach (var msg in nats.SubscribeAsync<string>(subject, cancellationToken: stop.Token))
                _ = AnswerAsync(msg);
        });

        // The subscription is live once a lookup comes back with a card. Polled rather than assumed:
        // it also waits out a circuit another fixture's unanswered lookups may have opened.
        var previews = Services.GetRequiredService<ILinkPreviewService>();
        var probe    = $"https://{ReadyHost}/warm-up/{Guid.NewGuid():N}";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);

        while ((await previews.ResolveAsync(probe, TimeSpan.FromSeconds(2))).Status != LinkPreviewStatus.Ready)
        {
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail("the stand-in crawler never answered on the crawler subject");
            await Task.Delay(250);
        }
    }

    [OneTimeTearDown]
    public async Task StopCrawler()
    {
        await stop.CancelAsync();
        try
        {
            await responder.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Cancelled on purpose.
        }
        stop.Dispose();
    }

    private async Task AnswerAsync(NatsMsg<string> msg)
    {
        var url   = JsonDocument.Parse(msg.Data!).RootElement.GetProperty("url").GetString()!;
        var host  = new Uri(url).Host;
        var count = asked.AddOrUpdate(url, 1, (_, n) => n + 1);

        // The first ask for a slow page is the send's, and it is left to time out.
        if (host is SlowHost or EmptyHost or GatedHost && count == 1)
            return;

        if (host is GatedHost)
            await gates.GetOrAdd(url, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        var reply = host is ReadyHost or SlowHost or GatedHost
            ? JsonSerializer.Serialize(new
            {
                url,
                title       = $"Title of {host}",
                description = "What the page says about itself",
                siteName    = "Argon Crawl Test",
                imageStored = "https://cdn.argon-crawl.test/card.webp",
                fromCache   = false
            })
            : JsonSerializer.Serialize(new { url, error = "the page has no metadata", code = "NO_METADATA" });

        await msg.ReplyAsync(reply);

        replied.GetOrAdd(url, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private static string UrlOn(string host) => $"https://{host}/page/{Guid.NewGuid():N}";

    private static IonArray<IMessageEntity> WithStub(string text, string url)
        => new([new MessageEntityLinkPreview(EntityType.LinkPreview, text.IndexOf(url, StringComparison.Ordinal), url.Length, 1, url,
            "client title", "client description", "client site", "https://evil.example/i.png", null)]);

    private async Task<(TestUserSession Owner, Guid SpaceId, Guid ChannelId)> RoomAsync(CancellationToken ct)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "links", ChannelType.Text, ct);
        return (owner, spaceId, channelId);
    }

    private static async Task<MessageEntityLinkPreview?> StoredCardAsync(Guid spaceId, Guid channelId, long messageId, CancellationToken ct)
        => (await StoredMessageAsync(spaceId, channelId, messageId, ct))?.Entities.OfType<MessageEntityLinkPreview>().SingleOrDefault();

    [Test, CancelAfter(120_000)]
    public async Task A_card_the_crawler_has_ready_goes_out_with_the_message(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var url  = UrlOn(ReadyHost);
        var text = $"read {url}";

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, text, WithStub(text, url), NextRandomId(), null, ct);
        var card      = await StoredCardAsync(spaceId, channelId, messageId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(card, Is.Not.Null, "the card was dropped although the crawler had it");
            Assert.That(card!.title, Is.EqualTo($"Title of {ReadyHost}"), "the title is the crawler's, not the client's");
            Assert.That(card.siteName, Is.EqualTo("Argon Crawl Test"));
            Assert.That(card.imageUrl, Is.EqualTo("https://cdn.argon-crawl.test/card.webp"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_card_still_being_fetched_follows_the_message_as_an_update(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var url  = UrlOn(SlowHost);
        var text = $"soon {url}";

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        await observer.SubscribeToChannel(channelId, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, text, WithStub(text, url), NextRandomId(), null, ct);

        var update = await observer.WaitForAsync<MessageUpdated>(e => e.message.messageId == messageId, EventWait, ct: ct);
        var card   = update.message.entities.Values.OfType<MessageEntityLinkPreview>().SingleOrDefault();

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(card?.title, Is.EqualTo($"Title of {SlowHost}"), "the update did not carry the card");
            Assert.That((await StoredCardAsync(spaceId, channelId, messageId, ct))?.title, Is.EqualTo($"Title of {SlowHost}"),
                "the card reached the clients but not the stored message");
            Assert.That(asked[url], Is.EqualTo(2), "the page was not asked for a second time after the send gave up");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_page_that_turns_out_to_have_nothing_loses_its_pending_stub(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var url  = UrlOn(EmptyHost);
        var text = $"nothing at {url}";

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        await observer.SubscribeToChannel(channelId, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, text, WithStub(text, url), NextRandomId(), null, ct);

        var pending = await StoredCardAsync(spaceId, channelId, messageId, ct);
        Assert.That(pending, Is.Not.Null.And.Property(nameof(MessageEntityLinkPreview.title)).Null,
            "premise: the message went out with a bare stub waiting on the crawler");

        var update = await observer.WaitForAsync<MessageUpdated>(e => e.message.messageId == messageId, EventWait, ct: ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(update.message.entities.Values.OfType<MessageEntityLinkPreview>(), Is.Empty);
            Assert.That(await StoredCardAsync(spaceId, channelId, messageId, ct), Is.Null, "the empty stub stayed on the stored message");
            Assert.That(update.message.text, Is.EqualTo(text));
        });
    }

    /// <summary>
    /// A late card for a message that has been deleted in the meantime must not bring it back.
    /// </summary>
    /// <remarks>
    /// <c>MessageUpdated</c> carries the whole message and the client replaces what it holds with it,
    /// so an update for a deleted message puts it back on screen for everyone in the channel.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_message_deleted_before_its_card_arrives_is_not_brought_back(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var url  = UrlOn(GatedHost);
        var text = $"regret {url}";

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        await observer.SubscribeToChannel(channelId, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, text, WithStub(text, url), NextRandomId(), null, ct);

        Assert.That(await owner.Channels.DeleteMessage(spaceId, channelId, messageId, ct), Is.InstanceOf<SuccessDeleteMessage>());
        var mark = observer.Mark();

        // Only now does the crawler answer the deferred lookup.
        gates.GetOrAdd(url, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        await replied.GetOrAdd(url, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task
           .WaitAsync(EventWait, ct);

        await observer.AssertNoneWithinAsync<MessageUpdated>(e => e.message.messageId == messageId, TimeSpan.FromSeconds(2),
            "a deleted message was sent back to the channel with its card", mark, ct);

        var stored = await StoredMessageAsync(spaceId, channelId, messageId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stored?.IsDeleted, Is.True);
            Assert.That(stored?.Entities.OfType<MessageEntityLinkPreview>().Single().title, Is.Null,
                "the late card was written into a deleted message");
        });
    }
}
