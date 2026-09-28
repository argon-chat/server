namespace ArgonComplexTest.Tests;

using System.Net;
using Argon.Features.BotApi;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using ion.runtime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static BotHttpKit;
using static ChannelTestKit;

/// <summary>
/// Custom emoji reactions through <c>IReactions/AddCustom</c> and <c>RemoveCustom</c>: any bot may use the emoji of the
/// space the message is in, only a verified one an emoji of another space, and the reaction events describe the emoji.
/// </summary>
[TestFixture]
public class BotReactionsTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(30);

    private const ArgonEntitlement Reacts = ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory | ArgonEntitlement.AddReactions;

    private static readonly byte[] EmojiPng = Png();

    private long randomId = Random.Shared.Next(1, int.MaxValue);

    private static byte[] Png()
    {
        using var image  = new Image<Rgba32>(100, 100, new Rgba32(220, 80, 40, 255));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    private static ISpaceExpressionInteraction ExpressionsOf(TestUserSession session)
        => session.Client.ForService<ISpaceExpressionInteraction>(Services);

    /// <summary>An emoji a person adds to their space, through the client API.</summary>
    private static async Task<ExpressionItem> EmojiAsync(TestUserSession owner, Guid spaceId, string name, CancellationToken ct)
    {
        var pack = await ExpressionsOf(owner).CreatePack(spaceId, ExpressionKind.Emoji, $"Pack {name}", $"pack{name}", ct);
        Assert.That(pack, Is.InstanceOf<SuccessPack>(), $"refused: {(pack as FailedPack)?.error}");

        var begun = await ExpressionsOf(owner).BeginUploadExpression(spaceId, ExpressionKind.Emoji, ExpressionFormat.Static, "image/png",
            EmojiPng.Length, ct);
        Assert.That(begun, Is.InstanceOf<SuccessUploadFile>(), $"the upload was not signed: {(begun as FailedUploadFile)?.error}");

        var ticket = (SuccessUploadFile)begun;
        using (var put = await TestObjectStore.UploadAsync(ticket.uploadUrl, EmojiPng, "image/png", ticket.formFields.Values.Select(f => (f.key, f.value))))
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var added = await ExpressionsOf(owner).AddItem(spaceId, ((SuccessPack)pack).pack.packId, ticket.blobId, null, name,
            new IonArray<string>(["🙂"]), IonArray<string>.Empty, null, ct);
        Assert.That(added, Is.InstanceOf<SuccessItem>(), $"refused: {(added as FailedItem)?.error}");
        return ((SuccessItem)added).item;
    }

    private async Task<(TestUserSession Owner, Guid SpaceId, Guid ChannelId, long MessageId)> RoomAsync(CancellationToken ct)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "reactions", ChannelType.Text, ct);
        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "react to me", new IonArray<IMessageEntity>([]),
            Interlocked.Increment(ref randomId), null, ct).Ok();

        return (owner, spaceId, channelId, messageId);
    }

    private static Task<Answer> AddCustomAsync(Bot bot, Guid spaceId, Guid channelId, long messageId, Guid itemId, CancellationToken ct)
        => PostAsync(bot, "/IReactions/v1/AddCustom", new { spaceId, channelId, messageId, itemId }, ct);

    private static Task<Answer> RemoveCustomAsync(Bot bot, Guid spaceId, Guid channelId, long messageId, Guid itemId, CancellationToken ct)
        => PostAsync(bot, "/IReactions/v1/RemoveCustom", new { spaceId, channelId, messageId, itemId }, ct);

    [Test, CancelAfter(180_000)]
    public async Task A_bot_reacts_with_its_spaces_emoji_and_the_events_describe_it(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId, messageId) = await RoomAsync(ct);
        var party = await EmojiAsync(owner, spaceId, "party", ct);
        var bot   = await InstallAsync(owner, spaceId, Reacts, ct);

        await using var events = await ChannelTestBot.BotEvents.OpenAsync(bot.Token, ct, (long)BotIntent.Reactions);

        var added   = await AddCustomAsync(bot, spaceId, channelId, messageId, party.itemId, ct);
        var twice   = await AddCustomAsync(bot, spaceId, channelId, messageId, party.itemId, ct);
        var unknown = await AddCustomAsync(bot, spaceId, channelId, messageId, Guid.NewGuid(), ct);
        var plain   = await SendAsync(bot, HttpMethod.Delete, "/IReactions/v1/Remove",
            JsonContent(new { channelId, messageId, emoji = ":party:" }), ct);

        var listed  = Ok(await GetAsync(bot, $"/IReactions/v1/List?channelId={channelId}&messageId={messageId}", ct));
        var batched = (await owner.Channels.BatchGetReactions(spaceId, channelId, new IonArray<long>([messageId]), ct)).Values.Single();
        var addEvent = await events.WaitForAsync("reactionAdd", e => e["messageId"]?.ToObject<long>() == messageId, EventWait, ct);

        var removed = await RemoveCustomAsync(bot, spaceId, channelId, messageId, party.itemId, ct);
        var again   = await RemoveCustomAsync(bot, spaceId, channelId, messageId, party.itemId, ct);
        var removeEvent = await events.WaitForAsync("reactionRemove", e => e["messageId"]?.ToObject<long>() == messageId, EventWait, ct);

        Assert.Multiple(() =>
        {
            Ok(added);
            Refused(twice, HttpStatusCode.Conflict, "already_reacted");
            Refused(unknown, HttpStatusCode.NotFound, "not_found", "an item that does not exist");
            Refused(plain, HttpStatusCode.NotFound, "reaction_not_found", "a unicode removal does not take a custom reaction");

            var reaction = listed.GetProperty("reactions").EnumerateArray().Single();
            Assert.That(reaction.GetProperty("emoji").GetString(), Is.EqualTo(":party:"));
            Assert.That(reaction.GetProperty("customEmojiId").GetGuid(), Is.EqualTo(party.itemId));
            Assert.That(batched.reactions.Values.Single().customEmojiId, Is.EqualTo(party.itemId), "ReactionInfo.customEmojiId");

            Assert.That(addEvent["emoji"]?.ToString(), Is.EqualTo(":party:"));
            Assert.That(addEvent["customEmojiId"]?.ToString(), Is.EqualTo(party.itemId.ToString()));
            Assert.That(addEvent["emojiName"]?.ToString(), Is.EqualTo("party"));
            Assert.That(addEvent["emojiUrl"]?.ToString(), Does.EndWith(party.fileId.ToString()));
            Assert.That(addEvent["userId"]?.ToString(), Is.EqualTo(bot.UserId.ToString()));

            Ok(removed);
            Refused(again, HttpStatusCode.NotFound, "reaction_not_found");
            Assert.That(removeEvent["customEmojiId"]?.ToString(), Is.EqualTo(party.itemId.ToString()));
            Assert.That(removeEvent["emojiName"]?.ToString(), Is.EqualTo("party"));
            Assert.That(removeEvent["emojiUrl"]?.ToString(), Does.EndWith(party.fileId.ToString()));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task Another_spaces_emoji_takes_a_verified_bot(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId, messageId) = await RoomAsync(ct);

        var elsewhere = await CreateSpaceAsync(owner, ct);
        var wave      = await EmojiAsync(owner, elsewhere, "wave", ct);

        var plainBot    = await InstallAsync(owner, spaceId, Reacts, ct);
        var verifiedBot = await InstallAsync(owner, spaceId, Reacts, ct, verified: true);

        var refused  = await AddCustomAsync(plainBot, spaceId, channelId, messageId, wave.itemId, ct);
        var accepted = await AddCustomAsync(verifiedBot, spaceId, channelId, messageId, wave.itemId, ct);
        var person   = await owner.Channels.AddCustomReaction(spaceId, channelId, messageId, wave.itemId, ct);

        var listed = Ok(await GetAsync(plainBot, $"/IReactions/v1/List?channelId={channelId}&messageId={messageId}", ct));
        var taken  = await RemoveCustomAsync(verifiedBot, spaceId, channelId, messageId, wave.itemId, ct);

        Assert.Multiple(() =>
        {
            Refused(refused, HttpStatusCode.Forbidden, "insufficient_permissions", "an unverified bot with a foreign emoji");
            Ok(accepted);
            Assert.That(person, Is.EqualTo(new FailedAddReaction(AddReactionError.INSUFFICIENT_PERMISSIONS)),
                "the client path never takes a foreign emoji");

            var reaction = listed.GetProperty("reactions").EnumerateArray().Single();
            Assert.That(reaction.GetProperty("emoji").GetString(), Is.EqualTo(":wave:"));
            Assert.That(reaction.GetProperty("customEmojiId").GetGuid(), Is.EqualTo(wave.itemId));
            Assert.That(reaction.GetProperty("userIds").EnumerateArray().Select(u => u.GetGuid()), Is.EqualTo(new[] { verifiedBot.UserId }));
            Ok(taken);
        });
    }

    private static StringContent JsonContent(object body)
        => new(System.Text.Json.JsonSerializer.Serialize(body, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        }), System.Text.Encoding.UTF8, "application/json");
}
