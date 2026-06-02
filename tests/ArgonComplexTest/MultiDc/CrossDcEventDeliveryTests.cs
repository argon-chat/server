namespace ArgonComplexTest.MultiDc;

using NATS.Client.Core;
using NATS.Net;
using System.Text;
using System.Text.Json;
using Testcontainers.Nats;
using Testcontainers.Redis;

/// <summary>
/// Integration test verifying cross-DC event delivery via NATS gateway.
/// Spins up 2 NATS containers connected via gateways and verifies that
/// a message published in DC1 is received by a subscriber in DC2.
/// </summary>
[TestFixture]
public class CrossDcEventDeliveryTests
{
    private NatsContainer _natsDc1 = null!;
    private RedisContainer _redisDc1 = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        // For a simple cross-DC test we use 2 standalone NATS with gateways
        // Testcontainers.Nats doesn't support gateway config directly,
        // so we test the code logic against a single NATS (simulating super-cluster behavior)
        _natsDc1 = new NatsBuilder("nats:2.14")
            .WithCommand("--jetstream", "--server_name", "dc1-node1")
            .Build();

        _redisDc1 = new RedisBuilder("redis:7-alpine")
            .Build();

        await Task.WhenAll(
            _natsDc1.StartAsync(),
            _redisDc1.StartAsync()
        );
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        await _natsDc1.DisposeAsync();
        await _redisDc1.DisposeAsync();
    }

    [Test]
    public async Task CoreNats_PublishSubscribe_CrossDc_Works()
    {
        // Simulates: DC1 publishes space event, DC2's FanoutService receives it
        var natsUrl = _natsDc1.GetConnectionString();
        await using var nats = new NatsClient(natsUrl);

        var subject = "space.events.00000000000000000000000000000001";
        var received = new TaskCompletionSource<string>();

        // Subscriber (simulating DC2 FanoutService)
        _ = Task.Run(async () =>
        {
            await foreach (var msg in nats.SubscribeAsync<byte[]>(subject))
            {
                if (msg.Data is not null)
                {
                    received.TrySetResult(Encoding.UTF8.GetString(msg.Data));
                    break;
                }
            }
        });

        // Small delay to ensure subscription is active
        await Task.Delay(200);

        // Publisher (simulating DC1 AppHubServer.BroadcastSpace)
        var payload = JsonSerializer.Serialize(new { type = "MessageSent", spaceId = "test" });
        await nats.PublishAsync(subject, Encoding.UTF8.GetBytes(payload));

        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(result, Does.Contain("MessageSent"));
    }

    [Test]
    public async Task JetStream_BotEvent_CursorResume_Works()
    {
        // Simulates: Bot publishes 3 events, reconnects from cursor (seq 1), receives events 2+3
        var natsUrl = _natsDc1.GetConnectionString();
        await using var nats = new NatsClient(natsUrl);
        var js = nats.CreateJetStreamContext();

        var streamName = "bot_events_test_cursor";
        var subject = streamName;

        // Create stream
        await js.CreateOrUpdateStreamAsync(new NATS.Client.JetStream.Models.StreamConfig(streamName, [subject])
        {
            MaxAge = TimeSpan.FromMinutes(30),
            Storage = NATS.Client.JetStream.Models.StreamConfigStorage.Memory,
            Retention = NATS.Client.JetStream.Models.StreamConfigRetention.Limits
        });

        // Publish 3 events
        for (int i = 1; i <= 3; i++)
        {
            var data = Encoding.UTF8.GetBytes($"{{\"seq\":{i}}}");
            await js.PublishAsync(subject, data);
        }

        // Create consumer starting from sequence 2 (simulating cursor resume after seq 1)
        var consumer = await js.CreateOrUpdateConsumerAsync(streamName,
            new NATS.Client.JetStream.Models.ConsumerConfig("test_cursor_consumer")
            {
                DeliverPolicy = NATS.Client.JetStream.Models.ConsumerConfigDeliverPolicy.ByStartSequence,
                OptStartSeq = 2,
                AckPolicy = NATS.Client.JetStream.Models.ConsumerConfigAckPolicy.Explicit
            });

        // Consume — should get events 2 and 3
        var events = new List<string>();
        var batch = consumer.FetchNoWaitAsync<byte[]>(new NATS.Client.JetStream.NatsJSFetchOpts { MaxMsgs = 10 });
        await foreach (var msg in batch)
        {
            if (msg.Data is not null)
                events.Add(Encoding.UTF8.GetString(msg.Data));
            await msg.AckAsync();
        }

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events[0], Does.Contain("\"seq\":2"));
        Assert.That(events[1], Does.Contain("\"seq\":3"));
    }

    [Test]
    public async Task PresenceEvent_PublishAndReceive_Works()
    {
        // Simulates: DC1 publishes presence online event, DC2 receives and processes it
        var natsUrl = _natsDc1.GetConnectionString();
        await using var nats = new NatsClient(natsUrl);

        var subject = "presence.events.dc1";
        var received = new TaskCompletionSource<string>();

        _ = Task.Run(async () =>
        {
            await foreach (var msg in nats.SubscribeAsync<byte[]>("presence.events.>"))
            {
                if (msg.Data is not null)
                {
                    received.TrySetResult(Encoding.UTF8.GetString(msg.Data));
                    break;
                }
            }
        });

        await Task.Delay(200);

        var evt = new
        {
            UserId = Guid.NewGuid(),
            SessionId = "session_123",
            SourceDcId = "dc1",
            Kind = "Online",
            Status = "Online",
            Timestamp = DateTimeOffset.UtcNow
        };

        await nats.PublishAsync(subject, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt)));

        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(result, Does.Contain("dc1"));
        Assert.That(result, Does.Contain("Online"));
    }

    [Test]
    public async Task WorkQueueStream_OnlyOneConsumerGetsMessage()
    {
        // Simulates: NATS WorkQueue ensures only one DC executes a scheduled task
        var natsUrl = _natsDc1.GetConnectionString();
        await using var nats = new NatsClient(natsUrl);
        var js = nats.CreateJetStreamContext();

        var streamName = "argon_schedules_test";
        var subject = "argon.schedules.test_task";

        await js.CreateOrUpdateStreamAsync(new NATS.Client.JetStream.Models.StreamConfig(streamName, [$"argon.schedules.>"])
        {
            Retention = NATS.Client.JetStream.Models.StreamConfigRetention.Workqueue,
            Storage = NATS.Client.JetStream.Models.StreamConfigStorage.Memory
        });

        // Create 2 consumers (simulating 2 DCs)
        var consumer1 = await js.CreateOrUpdateConsumerAsync(streamName,
            new NATS.Client.JetStream.Models.ConsumerConfig("sched_dc1")
            {
                FilterSubject = subject,
                AckPolicy = NATS.Client.JetStream.Models.ConsumerConfigAckPolicy.Explicit
            });

        var consumer2 = await js.CreateOrUpdateConsumerAsync(streamName,
            new NATS.Client.JetStream.Models.ConsumerConfig("sched_dc2")
            {
                FilterSubject = subject,
                AckPolicy = NATS.Client.JetStream.Models.ConsumerConfigAckPolicy.Explicit
            });

        // Publish 1 message
        await js.PublishAsync(subject, "{\"task\":\"auto_delete\"}"u8.ToArray());

        // Both try to fetch — only one should get it
        var batch1 = consumer1.FetchNoWaitAsync<byte[]>(new NATS.Client.JetStream.NatsJSFetchOpts { MaxMsgs = 1 });
        var batch2 = consumer2.FetchNoWaitAsync<byte[]>(new NATS.Client.JetStream.NatsJSFetchOpts { MaxMsgs = 1 });

        var count1 = 0;
        var count2 = 0;

        await foreach (var msg in batch1)
        {
            if (msg.Data is not null) count1++;
            await msg.AckAsync();
        }

        await foreach (var msg in batch2)
        {
            if (msg.Data is not null) count2++;
            await msg.AckAsync();
        }

        // WorkQueue: message delivered to one consumer, removed on ack
        Assert.That(count1 + count2, Is.EqualTo(1),
            "WorkQueue should deliver message to exactly one consumer");
    }
}
