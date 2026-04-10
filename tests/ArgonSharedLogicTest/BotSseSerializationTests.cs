namespace ArgonSharedLogicTest;

using System.Text;
using Argon.Entities;
using Argon.Features.BotApi;
using Argon.Services.Ion;
using ArgonContracts;
using ion.runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Tests for the Bot SSE serialization pipeline:
/// - Event data is serialized without internal fields (UnionKey, UnionIndex)
/// - IMessageEntity entities preserve all concrete type fields
/// - IonArray serializes as a plain JSON array
/// - Event names are camelCase
/// </summary>
[TestFixture]
public class BotSseSerializationTests
{
    private static readonly JsonSerializerSettings SseSettings = new()
    {
        ContractResolver = new BotSseContractResolver(),
        Formatting       = Formatting.None,
        Converters       = { new IonArrayConverter(), new IonMaybeConverter() }
    };

    // ──────── UnionKey / UnionIndex exclusion ────────

    [Test]
    public void MessageSent_ShouldNot_Contain_UnionKey()
    {
        var evt = CreateSampleMessageSentEvent();
        var json = JsonConvert.SerializeObject(evt, SseSettings);
        var jo = JObject.Parse(json);

        Assert.That(jo.ContainsKey("unionKey"), Is.False, "UnionKey should be excluded from SSE JSON");
        Assert.That(jo.ContainsKey("UnionKey"), Is.False, "UnionKey should be excluded from SSE JSON");
        Assert.That(jo.ContainsKey("unionIndex"), Is.False, "UnionIndex should be excluded from SSE JSON");
        Assert.That(jo.ContainsKey("UnionIndex"), Is.False, "UnionIndex should be excluded from SSE JSON");
    }

    [Test]
    public void MessageEntity_ShouldNot_Contain_UnionKey()
    {
        var entity = new MessageEntityBold(EntityType.Bold, 0, 5, 1);
        var json = JsonConvert.SerializeObject(entity, SseSettings);
        var jo = JObject.Parse(json);

        Assert.That(jo.ContainsKey("unionKey"), Is.False);
        Assert.That(jo.ContainsKey("unionIndex"), Is.False);
    }

    // ──────── Entity fields preserved ────────

    [Test]
    public void MessageEntityBold_PreservesAllFields()
    {
        var entity = new MessageEntityBold(EntityType.Bold, 3, 7, 1);
        var json = JsonConvert.SerializeObject(entity, SseSettings);
        var jo = JObject.Parse(json);

        Assert.That(jo["type"]?.Value<int>(), Is.EqualTo((int)EntityType.Bold));
        Assert.That(jo["offset"]?.Value<int>(), Is.EqualTo(3));
        Assert.That(jo["length"]?.Value<int>(), Is.EqualTo(7));
        Assert.That(jo["version"]?.Value<int>(), Is.EqualTo(1));
    }

    [Test]
    public void MessageEntityMention_PreservesUserId()
    {
        var userId = Guid.NewGuid();
        var entity = new MessageEntityMention(EntityType.Mention, 0, 10, 1, userId);
        var json = JsonConvert.SerializeObject(entity, SseSettings);
        var jo = JObject.Parse(json);

        Assert.That(jo["userId"]?.Value<string>(), Is.EqualTo(userId.ToString()));
        Assert.That(jo["offset"]?.Value<int>(), Is.EqualTo(0));
        Assert.That(jo["length"]?.Value<int>(), Is.EqualTo(10));
    }

    [Test]
    public void MessageEntityUrl_PreservesDomainAndPath()
    {
        var entity = new MessageEntityUrl(EntityType.Url, 5, 20, 1, "example.com", "/path");
        var json = JsonConvert.SerializeObject(entity, SseSettings);
        var jo = JObject.Parse(json);

        Assert.That(jo["domain"]?.Value<string>(), Is.EqualTo("example.com"));
        Assert.That(jo["path"]?.Value<string>(), Is.EqualTo("/path"));
    }

    [Test]
    public void MessageEntityAttachment_PreservesAllFields()
    {
        var fileId = Guid.NewGuid();
        var entity = new MessageEntityAttachment(
            EntityType.Attachment, 0, 0, 1,
            fileId, "image.png", 12345, "image/png", 800, 600, "abc123");
        var json = JsonConvert.SerializeObject(entity, SseSettings);
        var jo = JObject.Parse(json);

        Assert.That(jo["fileId"]?.Value<string>(), Is.EqualTo(fileId.ToString()));
        Assert.That(jo["fileName"]?.Value<string>(), Is.EqualTo("image.png"));
        Assert.That(jo["fileSize"]?.Value<long>(), Is.EqualTo(12345));
        Assert.That(jo["contentType"]?.Value<string>(), Is.EqualTo("image/png"));
        Assert.That(jo["width"]?.Value<int>(), Is.EqualTo(800));
        Assert.That(jo["height"]?.Value<int>(), Is.EqualTo(600));
        Assert.That(jo["thumbHash"]?.Value<string>(), Is.EqualTo("abc123"));
    }

    // ──────── IonArray<IMessageEntity> roundtrip ────────

    [Test]
    public void IonArray_Of_Entities_Serializes_As_JsonArray()
    {
        var entities = new IonArray<IMessageEntity>(new List<IMessageEntity>
        {
            new MessageEntityBold(EntityType.Bold, 0, 5, 1),
            new MessageEntityItalic(EntityType.Italic, 6, 3, 1)
        });

        var json = JsonConvert.SerializeObject(entities, SseSettings);
        var arr = JArray.Parse(json);

        Assert.That(arr.Count, Is.EqualTo(2));
        Assert.That(arr[0]["type"]?.Value<int>(), Is.EqualTo((int)EntityType.Bold));
        Assert.That(arr[0]["offset"]?.Value<int>(), Is.EqualTo(0));
        Assert.That(arr[0]["length"]?.Value<int>(), Is.EqualTo(5));

        Assert.That(arr[1]["type"]?.Value<int>(), Is.EqualTo((int)EntityType.Italic));
        Assert.That(arr[1]["offset"]?.Value<int>(), Is.EqualTo(6));
        Assert.That(arr[1]["length"]?.Value<int>(), Is.EqualTo(3));
    }

    [Test]
    public void IonArray_Empty_Serializes_As_EmptyArray()
    {
        var entities = new IonArray<IMessageEntity>(new List<IMessageEntity>());
        var json = JsonConvert.SerializeObject(entities, SseSettings);

        Assert.That(json, Is.EqualTo("[]"));
    }

    // ──────── Full MessageSent event ────────

    [Test]
    public void MessageSent_Full_Serialization_PreservesEntities()
    {
        var spaceId   = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var senderId  = Guid.NewGuid();

        var entities = new IonArray<IMessageEntity>(new List<IMessageEntity>
        {
            new MessageEntityBold(EntityType.Bold, 0, 5, 1),
            new MessageEntityMention(EntityType.Mention, 6, 8, 1, senderId),
        });

        var msg = new ArgonMessage(
            messageId: 123456789L,
            replyId: null,
            channelId: channelId,
            spaceId: spaceId,
            text: "Hello @user!",
            entities: entities,
            timeSent: DateTime.UtcNow,
            sender: senderId);

        var evt = new MessageSent(spaceId, msg);
        var json = JsonConvert.SerializeObject(evt, SseSettings);
        var jo = JObject.Parse(json);

        // No union internals at top level
        Assert.That(jo.ContainsKey("unionKey"), Is.False);
        Assert.That(jo.ContainsKey("unionIndex"), Is.False);

        // Message present with entities
        var message = jo["message"]!;
        Assert.That(message["text"]?.Value<string>(), Is.EqualTo("Hello @user!"));
        Assert.That(message["messageId"]?.Value<long>(), Is.EqualTo(123456789L));

        var entitiesArr = message["entities"] as JArray;
        Assert.That(entitiesArr, Is.Not.Null);
        Assert.That(entitiesArr!.Count, Is.EqualTo(2));

        // First entity: Bold with all fields
        Assert.That(entitiesArr[0]["type"]?.Value<int>(), Is.EqualTo((int)EntityType.Bold));
        Assert.That(entitiesArr[0]["offset"]?.Value<int>(), Is.EqualTo(0));
        Assert.That(entitiesArr[0]["length"]?.Value<int>(), Is.EqualTo(5));

        // Second entity: Mention with userId
        Assert.That(entitiesArr[1]["type"]?.Value<int>(), Is.EqualTo((int)EntityType.Mention));
        Assert.That(entitiesArr[1]["userId"]?.Value<string>(), Is.EqualTo(senderId.ToString()));

        // No union key on entities
        Assert.That(entitiesArr[0].Value<string>("unionKey"), Is.Null);
        Assert.That(entitiesArr[1].Value<string>("unionKey"), Is.Null);
    }

    [Test]
    public void MessageSent_NoEntities_HasEmptyArray()
    {
        var spaceId   = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var senderId  = Guid.NewGuid();

        var msg = new ArgonMessage(
            messageId: 1L,
            replyId: null,
            channelId: channelId,
            spaceId: spaceId,
            text: "No entities",
            entities: new IonArray<IMessageEntity>(new List<IMessageEntity>()),
            timeSent: DateTime.UtcNow,
            sender: senderId);

        var evt = new MessageSent(spaceId, msg);
        var json = JsonConvert.SerializeObject(evt, SseSettings);
        var jo = JObject.Parse(json);

        var entitiesArr = jo["message"]!["entities"] as JArray;
        Assert.That(entitiesArr, Is.Not.Null);
        Assert.That(entitiesArr!.Count, Is.EqualTo(0));
    }

    // ──────── No $type in SSE output ────────

    [Test]
    public void SseOutput_ShouldNot_Contain_DollarType()
    {
        var evt = CreateSampleMessageSentEvent();
        var json = JsonConvert.SerializeObject(evt, SseSettings);

        Assert.That(json, Does.Not.Contain("$type"), "SSE output must not contain $type (internal serialization detail)");
    }

    // ──────── NATS roundtrip preserves entities ────────

    [Test]
    public void NatsSerializer_Roundtrip_PreservesEntities()
    {
        var natsSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All,
            Formatting       = Formatting.None,
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            Converters       = { new MessageEntityConverter(), new IonArrayConverter(), new IonMaybeConverter() }
        };

        var spaceId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var entities = new IonArray<IMessageEntity>(new List<IMessageEntity>
        {
            new MessageEntityBold(EntityType.Bold, 0, 5, 1),
            new MessageEntityAttachment(EntityType.Attachment, 0, 0, 1,
                Guid.NewGuid(), "file.pdf", 9999, "application/pdf", null, null, null),
        });

        var original = new BotSseEvent
        {
            Id = "test_42",
            Type = BotEventType.MessageCreate,
            SpaceId = spaceId,
            Data = new MessageSent(spaceId, new ArgonMessage(
                42L, null, Guid.NewGuid(), spaceId, "test", entities, DateTime.UtcNow, senderId))
        };

        // Serialize (NATS write)
        var natsJson = JsonConvert.SerializeObject(original, natsSettings);

        // Deserialize (NATS read)
        var deserialized = JsonConvert.DeserializeObject<BotSseEvent>(natsJson, natsSettings)!;

        // Re-serialize for SSE output
        var sseJson = JsonConvert.SerializeObject(deserialized.Data, SseSettings);
        var jo = JObject.Parse(sseJson);

        var entitiesArr = jo["message"]!["entities"] as JArray;
        Assert.That(entitiesArr, Is.Not.Null);
        Assert.That(entitiesArr!.Count, Is.EqualTo(2), "Entities lost in NATS roundtrip");

        // Bold entity preserved
        Assert.That(entitiesArr[0]["type"]?.Value<int>(), Is.EqualTo((int)EntityType.Bold));
        Assert.That(entitiesArr[0]["offset"]?.Value<int>(), Is.EqualTo(0));

        // Attachment entity preserved with extra fields
        Assert.That(entitiesArr[1]["fileName"]?.Value<string>(), Is.EqualTo("file.pdf"));
        Assert.That(entitiesArr[1]["fileSize"]?.Value<long>(), Is.EqualTo(9999));
        Assert.That(entitiesArr[1]["contentType"]?.Value<string>(), Is.EqualTo("application/pdf"));

        // No $type in SSE output
        Assert.That(sseJson, Does.Not.Contain("$type"));

        // No UnionKey in SSE output
        Assert.That(sseJson, Does.Not.Contain("unionKey"));
        Assert.That(sseJson, Does.Not.Contain("unionIndex"));
    }

    // ──────── camelCase event names ────────

    [Test]
    [TestCase(BotEventType.Ready, "ready")]
    [TestCase(BotEventType.Heartbeat, "heartbeat")]
    [TestCase(BotEventType.MessageCreate, "messageCreate")]
    [TestCase(BotEventType.MemberJoin, "memberJoin")]
    [TestCase(BotEventType.VoiceStreamStart, "voiceStreamStart")]
    [TestCase(BotEventType.PresenceUpdate, "presenceUpdate")]
    [TestCase(BotEventType.DirectMessageCreate, "directMessageCreate")]
    [TestCase(BotEventType.ArchetypeChanged, "archetypeChanged")]
    public void EventType_ToCamelCase_IsCorrect(BotEventType type, string expected)
    {
        var s = type.ToString();
        var camel = char.ToLowerInvariant(s[0]) + s[1..];
        Assert.That(camel, Is.EqualTo(expected));
    }

    // ──────── Helpers ────────

    private static MessageSent CreateSampleMessageSentEvent()
    {
        var spaceId = Guid.NewGuid();
        var entities = new IonArray<IMessageEntity>(new List<IMessageEntity>
        {
            new MessageEntityBold(EntityType.Bold, 0, 5, 1),
        });

        return new MessageSent(spaceId, new ArgonMessage(
            1L, null, Guid.NewGuid(), spaceId, "Hello",
            entities, DateTime.UtcNow, Guid.NewGuid()));
    }
}
