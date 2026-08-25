namespace ArgonSharedLogicTest.Cache;

using Argon.ArchetypeModel;
using Argon.Grains;
using ArgonContracts;

/// <summary>
/// The token for a space's channels has to identify the channels, not the traffic in them.
/// </summary>
/// <remarks>
/// <c>lastMessageId</c> used to be inside the hash, so in any space where anyone was talking the
/// token changed every time the two-minute cache entry refilled. That token is what gates the
/// expensive half of <c>GetSnapshot</c> — one grain call per visible channel — so the versioned
/// bootstrap that exists to avoid the fan-out never once avoided it.
/// <para>
/// Nothing about the answer is wrong when this regresses, only the cost, which is why it needs a
/// test of its own: every assertion about snapshot <em>content</em> passes either way.
/// </para>
/// <para>
/// Both directions are pinned. A token that never changed would pass the first test here and serve a
/// renamed channel forever; a token that always changed would pass the rest and defeat the dedupe.
/// </para>
/// </remarks>
[TestFixture]
public class ChannelsVersionTests
{
    private static readonly Guid SpaceId   = Guid.Parse("d3f1a6b0-0000-4000-8000-000000000001");
    private static readonly Guid ChannelId = Guid.Parse("d3f1a6b0-0000-4000-8000-000000000002");
    private static readonly Guid SiblingId = Guid.Parse("d3f1a6b0-0000-4000-8000-000000000003");
    private static readonly Guid RoleId    = Guid.Parse("d3f1a6b0-0000-4000-8000-000000000004");

    [Test]
    public void Talking_in_a_channel_leaves_the_token_alone()
        => Assert.That(CachedChannel.VersionOf(Channels(lastMessageId: 4_218_665_144_320)),
            Is.EqualTo(CachedChannel.VersionOf(Channels(lastMessageId: 1))),
            "two channel lists differing only in how recently someone posted are the same list");

    [Test]
    public void A_channel_with_no_messages_and_one_with_thousands_hash_alike()
        => Assert.That(CachedChannel.VersionOf(Channels(lastMessageId: 9_999)),
            Is.EqualTo(CachedChannel.VersionOf(Channels(lastMessageId: 0))));

    [Test]
    public void Renaming_a_channel_changes_the_token()
        => Assert.That(CachedChannel.VersionOf(Channels(lastMessageId: 1, name: "general-chat")),
            Is.Not.EqualTo(CachedChannel.VersionOf(Channels(lastMessageId: 1))));

    [Test]
    public void Changing_a_permission_overwrite_changes_the_token()
        => Assert.That(CachedChannel.VersionOf(Channels(lastMessageId: 1, deny: ArgonEntitlement.SendMessages)),
            Is.Not.EqualTo(CachedChannel.VersionOf(Channels(lastMessageId: 1))),
            "the overwrites decide who sees the channel, so they have to be inside the token");

    [Test]
    public void Adding_a_channel_changes_the_token()
    {
        var one = Channels(lastMessageId: 1);
        var two = Channels(lastMessageId: 1);

        two.Add(new CachedChannel(
            new ArgonChannel(ChannelType.Voice, SpaceId, SiblingId, "voice", null, null, "a1", 0, null), []));

        Assert.That(CachedChannel.VersionOf(two), Is.Not.EqualTo(CachedChannel.VersionOf(one)));
    }

    /// <summary>
    /// One channel, built so that every argument a test wants to vary is an argument and everything
    /// else is fixed — a random guid anywhere in here would make "differ only in X" untrue.
    /// </summary>
    private static List<CachedChannel> Channels(
        long             lastMessageId,
        string           name = "general",
        ArgonEntitlement deny = ArgonEntitlement.None)
        =>
        [
            new CachedChannel(
                new ArgonChannel(ChannelType.Text, SpaceId, ChannelId, name, null, null, "a0", lastMessageId, null),
                [new CachedOverwrite(IArchetypeScope.Archetype, RoleId, null, ArgonEntitlement.ViewChannel, deny)])
        ];
}
