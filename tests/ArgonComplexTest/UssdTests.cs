namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Core.Grains.Interfaces;
using Argon.Entities;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// <c>CallInteraction.UssdExecute</c>: the dial-pad codes a user types, today only feature-flag activation.
/// </summary>
[TestFixture]
public class UssdTests : TestBase
{
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Empty_and_unknown_codes_are_refused(CancellationToken ct = default)
    {
        var user  = await CreateSessionAsync(ct);
        var calls = CallsOf(user);

        var empty   = await calls.UssdExecute("   ", Guid.NewGuid(), ct);
        var unknown = await calls.UssdExecute($"*{Random.Shared.Next(100_000, 999_999)}#", Guid.NewGuid(), ct);

        Assert.Multiple(() =>
        {
            Assert.That(empty.success, Is.False);
            Assert.That(empty.message, Is.EqualTo("Empty USSD command"));
            Assert.That(unknown.success, Is.False);
            Assert.That(unknown.message, Is.EqualTo("Unknown USSD command"));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_flag_code_activates_the_flag_for_the_caller_and_tells_their_sessions(CancellationToken ct = default)
    {
        var user   = await CreateSessionAsync(ct);
        var flagId = $"test.ussd.{Guid.NewGuid():N}";
        var code   = $"*{Random.Shared.Next(100_000, 999_999)}#";
        await CreateFlagAsync(flagId, code);

        await using var inbox = await RealtimeClient.ConnectAsync(user, ct);
        var mark = inbox.Mark();

        var result = await CallsOf(user).UssdExecute($" {code} ", Guid.NewGuid(), ct);

        var activated = await inbox.WaitForAsync<FeatureFlagActivated>(e => e.flagId == flagId, TimeSpan.FromSeconds(15), mark, ct);
        var evaluated = await GetGrainFactory().GetGrain<IFeatureFlagGrain>(Guid.Empty)
           .EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(user.UserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.success, Is.True, result.message);
            Assert.That(activated.userId, Is.EqualTo(user.UserId));
            Assert.That(activated.isEnabled, Is.True);
            Assert.That(evaluated.IsEnabled, Is.True);
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task The_code_of_an_expired_flag_does_not_activate_it(CancellationToken ct = default)
    {
        var user   = await CreateSessionAsync(ct);
        var flagId = $"test.ussd.{Guid.NewGuid():N}";
        var code   = $"*{Random.Shared.Next(100_000, 999_999)}#";
        await CreateFlagAsync(flagId, code);

        await using (var db = await FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync(ct))
            await db.FeatureFlags.Where(f => f.Id == flagId)
               .ExecuteUpdateAsync(s => s.SetProperty(f => f.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)), ct);

        var result = await CallsOf(user).UssdExecute(code, Guid.NewGuid(), ct);

        Assert.Multiple(() =>
        {
            Assert.That(result.success, Is.False);
            Assert.That(result.message, Is.EqualTo("Activation failed"));
        });
    }

    private ICallInteraction CallsOf(TestUserSession session)
        => session.Client.ForService<ICallInteraction>(FactoryAsp.Services);

    private async Task CreateFlagAsync(string flagId, string code)
    {
        var created = await GetGrainFactory().GetGrain<IFeatureFlagGrain>(Guid.Empty)
           .CreateFlagAsync(new FeatureFlagInput(flagId, "ussd test", false, null, null, code, null));
        Assert.That(created.Success, Is.True, created.Error);
    }
}
