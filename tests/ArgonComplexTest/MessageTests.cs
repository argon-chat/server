namespace ArgonComplexTest.Tests;

using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;
using ion.runtime;

[TestFixture, Parallelizable(ParallelScope.None)]
public class MessageTests : TestBase
{
    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task SendMessage_ToTextChannel_ReturnsMessageId(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "general", ct);

        var messageId = await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId,
            channelId,
            "Hello, World!",
            new IonArray<IMessageEntity>([]),
            1,
            null,
            ct);

        Assert.That(messageId, Is.GreaterThan(0));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task SendMultipleMessages_ThenQuery_ReturnsAllMessages(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "chat", ct);

        var message1 = "First message";
        var message2 = "Second message";
        var message3 = "Third message";

        await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, message1, new ion.runtime.IonArray<IMessageEntity>([]), 1, null, ct);

        await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, message2, new ion.runtime.IonArray<IMessageEntity>([]), 2, null, ct);

        await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, message3, new ion.runtime.IonArray<IMessageEntity>([]), 3, null, ct);

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        Assert.That(messages.Values.Count, Is.EqualTo(3), "Expected 3 messages");

        // Messages are returned in DESC order (newest first)
        Assert.That(messages.Values[0].text, Is.EqualTo(message3));
        Assert.That(messages.Values[1].text, Is.EqualTo(message2));
        Assert.That(messages.Values[2].text, Is.EqualTo(message1));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(2)]
    public async Task SendMessageWithReply_ReturnsMessageWithReplyId(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "replies", ct);

        var originalMessageId = await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, "Original message", new IonArray<IMessageEntity>([]), 1, null, ct);

        var replyMessageId = await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, "Reply to original", new IonArray<IMessageEntity>([]), 2, originalMessageId, ct);

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        var replyMessage = messages.Values.FirstOrDefault(m => m.messageId == replyMessageId);

        Assert.That(replyMessage, Is.Not.Null, "Reply message not found");
        Assert.That(replyMessage!.replyId, Is.EqualTo(originalMessageId));
        Assert.That(replyMessage.text, Is.EqualTo("Reply to original"));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(3)]
    public async Task QueryMessages_WithLimit_ReturnsLimitedMessages(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "limited", ct);

        for (int i = 1; i <= 5; i++)
        {
            await GetChannelService(scope.ServiceProvider).SendMessage(
                spaceId, channelId, $"Message {i}", new IonArray<IMessageEntity>([]), i, null, ct);
        }

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 3, ct);

        Assert.That(messages.Values.Count, Is.EqualTo(3), "Expected exactly 3 messages with limit=3");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(4)]
    public async Task SendMessage_VerifySenderAndTimestamp(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user      = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "metadata", ct);

        var beforeSend = DateTime.UtcNow;

        await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, "Test message", new IonArray<IMessageEntity>([]), 1, null, ct);

        var afterSend = DateTime.UtcNow;

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        var message = messages.Values.FirstOrDefault();

        Assert.That(message, Is.Not.Null);
        Assert.That(message!.sender, Is.EqualTo(user.userId));
        Assert.That(message.timeSent, Is.GreaterThanOrEqualTo(beforeSend));
        Assert.That(message.timeSent, Is.LessThanOrEqualTo(afterSend));
        Assert.That(message.channelId, Is.EqualTo(channelId));
        Assert.That(message.spaceId, Is.EqualTo(spaceId));
    }


    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task SendMessage_WithMentionEntity_ReturnsMentionInMessage(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user      = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "mentions", ct);

        var messageText = "Hello @user, how are you?";
        var mentionEntity = new MessageEntityMention(
            EntityType.Mention,
            offset: 6, // "@user" starts at position 6
            length: 5, // "@user" is 5 characters
            version: 1,
            userId: user.userId
        );

        var entities = new IonArray<IMessageEntity>([mentionEntity]);

        await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, messageText, entities, randomId: 1, replyTo: null, ct);

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        var message = messages.Values.FirstOrDefault();

        Assert.That(message, Is.Not.Null);
        Assert.That(message!.text, Is.EqualTo(messageText));
        Assert.That(message.entities.Values.Count, Is.EqualTo(1));

        var retrievedEntity = message.entities.Values[0] as MessageEntityMention;
        Assert.That(retrievedEntity, Is.Not.Null);
        Assert.That(retrievedEntity!.userId, Is.EqualTo(user.userId));
        Assert.That(retrievedEntity.offset, Is.EqualTo(6));
        Assert.That(retrievedEntity.length, Is.EqualTo(5));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task SendMessage_WithMultipleEntities_ReturnsAllEntities(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user      = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "rich-text", ct);

        var messageText = "Check @user and visit https://example.com #cool";

        var entities = new IonArray<IMessageEntity>([
            new MessageEntityMention(EntityType.Mention, 6, 5, 1, user.userId),
            new MessageEntityUrl(EntityType.Url, 22, 19, 1, "example.com", "/"),
            new MessageEntityHashTag(EntityType.Hashtag, 42, 5, 1, "cool")
        ]);

        await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, messageText, entities, randomId: 1, replyTo: null, ct);

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        var message = messages.Values.FirstOrDefault();

        Assert.That(message, Is.Not.Null);
        Assert.That(message!.entities.Values.Count, Is.EqualTo(3));

        Assert.That(message.entities.Values[0], Is.TypeOf<MessageEntityMention>());
        Assert.That(message.entities.Values[1], Is.TypeOf<MessageEntityUrl>());
        Assert.That(message.entities.Values[2], Is.TypeOf<MessageEntityHashTag>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(2)]
    public async Task SendMessage_WithUrlEntity_ExtractsDomainAndPath(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "links", ct);

        var messageText = "Visit https://github.com/argon-chat/server for code";
        var urlEntity = new MessageEntityUrl(
            EntityType.Url,
            offset: 6,
            length: 36,
            version: 1,
            domain: "github.com",
            path: "/argon-chat/server"
        );

        await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, messageText, new IonArray<IMessageEntity>([urlEntity]), 1, null, ct);

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        var message = messages.Values.FirstOrDefault();
        var entity  = message!.entities.Values[0] as MessageEntityUrl;

        Assert.That(entity, Is.Not.Null);
        Assert.That(entity!.domain, Is.EqualTo("github.com"));
        Assert.That(entity.path, Is.EqualTo("/argon-chat/server"));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(3)]
    public async Task QueryMessages_WithFromParameter_ReturnsPaginatedResults(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "pagination", ct);

        // Отправляем 10 сообщений
        var messageIds = new List<long>();
        for (int i = 1; i <= 10; i++)
        {
            var id = await GetChannelService(scope.ServiceProvider).SendMessage(
                spaceId, channelId, $"Message {i}", new IonArray<IMessageEntity>([]), i, null, ct);
            messageIds.Add(id);
        }

        // Получаем первую страницу (последние 5 сообщений)
        var firstPage = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, from: null, limit: 5, ct);

        Assert.That(firstPage.Values.Count, Is.EqualTo(5));
        Assert.That(firstPage.Values[0].text, Is.EqualTo("Message 10")); // Новейшее

        // Получаем вторую страницу (следующие 5 сообщений)
        var oldestFromFirstPage = firstPage.Values.Last().messageId;
        var secondPage = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, from: oldestFromFirstPage, limit: 5, ct);

        Assert.That(secondPage.Values.Count, Is.EqualTo(5));
        Assert.That(secondPage.Values[0].text, Is.EqualTo("Message 5"));
        Assert.That(secondPage.Values[4].text, Is.EqualTo("Message 1")); // Старейшее
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(4)]
    public async Task SendMessage_WithReplyChain_MaintainsReplyRelationships(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "threads", ct);

        // Создаём цепочку ответов
        var msg1 = await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, "Original message", new IonArray<IMessageEntity>([]), 1, null, ct);

        var msg2 = await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, "Reply to original", new IonArray<IMessageEntity>([]), 2, msg1, ct);

        var msg3 = await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, "Reply to reply", new IonArray<IMessageEntity>([]), 3, msg2, ct);

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        // Проверяем цепочку (обратный порядок - новые сверху)
        var retrievedMsg3 = messages.Values.FirstOrDefault(m => m.messageId == msg3);
        var retrievedMsg2 = messages.Values.FirstOrDefault(m => m.messageId == msg2);
        var retrievedMsg1 = messages.Values.FirstOrDefault(m => m.messageId == msg1);

        Assert.That(retrievedMsg3!.replyId, Is.EqualTo(msg2));
        Assert.That(retrievedMsg2!.replyId, Is.EqualTo(msg1));
        Assert.That(retrievedMsg1!.replyId, Is.Null);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(5)]
    public async Task SendMessage_WithEmailEntity_PreservesEmailAddress(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "contacts", ct);

        var messageText = "Contact me at user@example.com for details";
        var emailEntity = new MessageEntityEmail(
            EntityType.Email,
            offset: 14,
            length: 16,
            version: 1,
            email: "user@example.com"
        );

        await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, messageText, new IonArray<IMessageEntity>([emailEntity]), 1, null, ct);

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        var entity = messages.Values[0].entities.Values[0] as MessageEntityEmail;

        Assert.That(entity, Is.Not.Null);
        Assert.That(entity!.email, Is.EqualTo("user@example.com"));
        Assert.That(entity.type, Is.EqualTo(EntityType.Email));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(6)]
    public async Task SendMessage_EmptyText_ShouldStillWork(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "attachments", ct);

        // Сообщение с пустым текстом (например, только вложения)
        var messageId = await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, "", new IonArray<IMessageEntity>([]), 1, null, ct);

        Assert.That(messageId, Is.GreaterThan(0));

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        Assert.That(messages.Values.Count, Is.EqualTo(1));
        Assert.That(messages.Values[0].text, Is.Empty);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(7)]
    public async Task SendMessage_VerifyMessageIdIsMonotonic(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "ordering", ct);

        // Отправляем несколько сообщений подряд
        var ids = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var id = await GetChannelService(scope.ServiceProvider).SendMessage(
                spaceId, channelId, $"Msg {i}", new IonArray<IMessageEntity>([]), i, null, ct);
            ids.Add(id);
        }

        // Проверяем что messageId монотонно возрастает
        for (int i = 1; i < ids.Count; i++)
        {
            Assert.That(ids[i], Is.GreaterThan(ids[i - 1]),
                $"Message ID should be monotonically increasing: {ids[i - 1]} -> {ids[i]}");
        }
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(8)]
    public async Task SendMessage_WithHashtagEntity_PreservesHashtag(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "hashtags", ct);

        var messageText = "This is #awesome and #cool!";
        var entities = new IonArray<IMessageEntity>([
            new MessageEntityHashTag(EntityType.Hashtag, 8, 8, 1, "awesome"),
            new MessageEntityHashTag(EntityType.Hashtag, 21, 5, 1, "cool")
        ]);

        await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, messageText, entities, 1, null, ct);

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        var message = messages.Values[0];
        Assert.That(message.entities.Values.Count, Is.EqualTo(2));

        var tag1 = message.entities.Values[0] as MessageEntityHashTag;
        var tag2 = message.entities.Values[1] as MessageEntityHashTag;

        Assert.That(tag1!.hashtag, Is.EqualTo("awesome"));
        Assert.That(tag2!.hashtag, Is.EqualTo("cool"));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(9)]
    public async Task QueryMessages_EmptyChannel_ReturnsEmptyList(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "empty", ct);

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        Assert.That(messages.Values.Count, Is.EqualTo(0));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(10)]
    public async Task SendMessage_LongText_HandlesCorrectly(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "long-messages", ct);

        // Создаём длинное сообщение (2000 символов)
        var longText = string.Join(" ", Enumerable.Repeat("This is a test message.", 100));

        var messageId = await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, longText, new IonArray<IMessageEntity>([]), 1, null, ct);

        Assert.That(messageId, Is.GreaterThan(0));

        var messages = await GetChannelService(scope.ServiceProvider).QueryMessages(
            spaceId, channelId, null, 10, ct);

        Assert.That(messages.Values[0].text, Is.EqualTo(longText));
        Assert.That(messages.Values[0].text.Length, Is.GreaterThan(1000));
    }
}