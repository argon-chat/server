namespace ArgonComplexTest;

using Argon.Core.Features.Transport;
using ArgonComplexTest.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What a replay cursor is allowed to mean, against the real Redis the buffer runs on.
/// </summary>
/// <remarks>
/// <para>The regression these guard is a silent one. A stream entry id is
/// <c>&lt;unixMs&gt;-&lt;seq&gt;</c> taken off the wall clock, so a cursor another region's Redis
/// issued has the same shape and the same age as one of ours and passes every check that looks only
/// at the cursor. The buffer used to run the range read against the local stream anyway and report
/// no gap: a client failed over between regions was told it was caught up while it had in fact
/// missed everything in between.</para>
///
/// <para>This needs a real Redis — the buffer is XADD and XRANGE and nothing else — so it lives here
/// rather than in the fast suite.</para>
/// </remarks>
[TestFixture]
public class RealtimeReplayBufferTests
{
    private static IRealtimeReplayBuffer Buffer
        => ArgonTestEnvironment.Instance.Host.Services.GetRequiredService<IRealtimeReplayBuffer>();

    /// <summary>An id of the right shape, well inside the retention window, that this stream never issued.</summary>
    /// <remarks>
    /// Built by bumping the sequence half of the newest real id, which is what makes it the dangerous
    /// case rather than a harmless one: it sorts <em>after</em> everything in the stream, so the range
    /// read finds nothing beyond it and — before the anchor probe — that read back as "caught up".
    /// A synthetic id sorting <em>before</em> the entries would have over-delivered instead, and the
    /// client dedupes by id, so it would have proved nothing.
    /// </remarks>
    private static string NeverIssuedAfter(string entryId)
    {
        var dash = entryId.LastIndexOf('-');
        return $"{entryId[..dash]}-{long.Parse(entryId[(dash + 1)..]) + 1}";
    }

    [Test]
    public async Task A_cursor_this_stream_never_issued_is_a_gap()
    {
        var userId = Guid.NewGuid();

        await Buffer.AppendUserAsync(userId, new byte[] { 1 });
        var newest = await Buffer.AppendUserAsync(userId, new byte[] { 2 });

        var result = await Buffer.ReadUserSinceAsync(userId, NeverIssuedAfter(newest));

        Assert.Multiple(() =>
        {
            Assert.That(result.Gap, Is.True,
                "a cursor issued by another region's stream must resync, not read as caught up");
            Assert.That(result.Entries, Is.Empty);
        });
    }

    [Test]
    public async Task A_cursor_this_stream_did_issue_replays_what_followed_it()
    {
        var userId = Guid.NewGuid();

        var first = await Buffer.AppendUserAsync(userId, new byte[] { 1 });
        await Buffer.AppendUserAsync(userId, new byte[] { 2 });
        await Buffer.AppendUserAsync(userId, new byte[] { 3 });

        var result = await Buffer.ReadUserSinceAsync(userId, first);

        Assert.Multiple(() =>
        {
            Assert.That(result.Gap, Is.False,
                "the anchor is present, which is what proves the tail behind it was never trimmed");
            Assert.That(result.Entries.Select(e => e.Payload[0]).ToArray(), Is.EqualTo(new byte[] { 2, 3 }),
                "strictly after the cursor, in order");
        });
    }

    [Test]
    public async Task A_cursor_older_than_the_retention_window_is_a_gap()
    {
        var userId = Guid.NewGuid();
        await Buffer.AppendUserAsync(userId, new byte[] { 1 });

        // Unix millisecond 1: unreplayable by age alone, and rejected without asking Redis at all.
        var result = await Buffer.ReadUserSinceAsync(userId, "1-0");

        Assert.Multiple(() =>
        {
            Assert.That(result.Gap, Is.True);
            Assert.That(result.Entries, Is.Empty);
        });
    }

    [Test]
    public async Task No_cursor_is_a_first_connect_rather_than_a_gap()
    {
        var userId = Guid.NewGuid();
        await Buffer.AppendUserAsync(userId, new byte[] { 1 });

        var result = await Buffer.ReadUserSinceAsync(userId, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Gap, Is.False,
                "a client with no cursor has just loaded fresh state; a forced resync would be noise");
            Assert.That(result.Entries, Is.Empty);
        });
    }

    /// <summary>Space streams take the same path, and are what a reconnecting client resumes most of.</summary>
    [Test]
    public async Task A_foreign_cursor_on_a_space_stream_is_a_gap_too()
    {
        var spaceId = Guid.NewGuid();

        var only = await Buffer.AppendSpaceAsync(spaceId, new byte[] { 1 });

        var result = await Buffer.ReadSpaceSinceAsync(spaceId, NeverIssuedAfter(only));

        Assert.That(result.Gap, Is.True);
    }
}
