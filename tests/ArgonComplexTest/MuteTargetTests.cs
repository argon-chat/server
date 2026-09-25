namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Core.Features.Logic;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Muting a space or a channel: every level and target kind the client can send lands as the
/// matching row, a second mute replaces the first, and unmuting removes it.
/// </summary>
[TestFixture]
public class MuteTargetTests : TestBase
{
    private static IMuteSettingsService Mutes => SocialHarness.Services.GetRequiredService<IMuteSettingsService>();

    [Test, CancelAfter(120_000)]
    public async Task A_channel_muted_for_mentions_is_stored_with_its_expiry(CancellationToken ct = default)
    {
        var alice   = await CreateSessionAsync(ct);
        var channel = Guid.NewGuid();
        var expiry  = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(2), DateTimeKind.Utc);

        await alice.Users.MuteTarget(channel, MuteTargetKind.Channel, MuteLevelType.OnlyMentions, suppressEveryone: true, expiry, ct);

        var row = (await Mutes.GetMuteSettingsAsync(alice.UserId, ct)).Single(m => m.TargetId == channel);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(row.TargetType, Is.EqualTo(MuteTargetType.Channel));
            Assert.That(row.MuteLevel, Is.EqualTo(MuteLevel.OnlyMentions));
            Assert.That(row.SuppressEveryone, Is.True);
            Assert.That(row.MuteExpiresAt, Is.EqualTo(new DateTimeOffset(expiry)).Within(TimeSpan.FromSeconds(1)));
            Assert.That(await Mutes.IsMutedAsync(alice.UserId, channel, ct), Is.False, "mentions-only is not a full mute");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Muting_again_replaces_the_level_and_unmuting_removes_it(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var space = Guid.NewGuid();

        await alice.Users.MuteTarget(space, MuteTargetKind.Space, MuteLevelType.All, suppressEveryone: false, null, ct);
        Assert.That(await Mutes.IsMutedAsync(alice.UserId, space, ct), Is.True);

        await alice.Users.MuteTarget(space, MuteTargetKind.Space, MuteLevelType.None, suppressEveryone: false, null, ct);

        var row = (await Mutes.GetMuteSettingsAsync(alice.UserId, ct)).Single(m => m.TargetId == space);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(row.TargetType, Is.EqualTo(MuteTargetType.Space));
            Assert.That(row.MuteLevel, Is.EqualTo(MuteLevel.None), "the second mute has to replace the first");
            Assert.That(row.MuteExpiresAt, Is.Null);
            Assert.That(await Mutes.IsMutedAsync(alice.UserId, space, ct), Is.False);
        });

        await alice.Users.UnmuteTarget(space, ct);

        Assert.That((await Mutes.GetMuteSettingsAsync(alice.UserId, ct)).Any(m => m.TargetId == space), Is.False);
    }
}
