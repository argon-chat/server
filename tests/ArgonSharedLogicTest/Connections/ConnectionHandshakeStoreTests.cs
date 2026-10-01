namespace ArgonSharedLogicTest.Connections;

using Argon.Features.Integrations.Connections;
using Argon.Services;
using ArgonContracts;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using static ConnectionsTestOptions;

/// <summary>
/// The handshake's Redis records: a state is good once, a parked result is good once, its token is
/// sealed while it waits, and the two counters count.
/// </summary>
[TestFixture]
public class ConnectionHandshakeStoreTests
{
    private static readonly ConnectionIdentity Identity = new("583231", "octocat", "https://github.com/octocat", null);
    private static readonly ProviderToken      Token    = new("gho_abc", null, null, "");

    private static ConnectionHandshakeStore Store(ConnectionsOptions? options = null)
    {
        var wrapped = Options.Create(options ?? Default());
        var cache   = new InMemoryArgonCacheDatabase(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

        return new ConnectionHandshakeStore(cache, new TokenSealer(wrapped), wrapped);
    }

    private static ConnectionHandshakeStore.Started Started(string state)
        => new(Guid.NewGuid(), Guid.NewGuid(), ConnectionProvider.GITHUB, state, "verifier", "https://api.argon.test/connections/callback/github",
            ConnectReturnKind.DESKTOP, false, DateTimeOffset.UtcNow);

    [Test]
    public async Task A_state_is_consumed_exactly_once()
    {
        var store   = Store();
        var started = Started(OAuthCodeFlow.NewState());

        await store.PutStartedAsync(started);

        var first  = await store.ConsumeStartedAsync(started.State);
        var second = await store.ConsumeStartedAsync(started.State);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(started));
            Assert.That(second, Is.Null, "a replayed callback finds the tombstone");
        });
    }

    [Test]
    public async Task An_unknown_or_absurd_state_finds_nothing()
    {
        var store = Store();

        Assert.Multiple(async () =>
        {
            Assert.That(await store.ConsumeStartedAsync("never-issued"), Is.Null);
            Assert.That(await store.ConsumeStartedAsync(""), Is.Null);
            Assert.That(await store.ConsumeStartedAsync(new string('x', 200)), Is.Null);
        });
    }

    [Test]
    public async Task A_parked_result_can_be_looked_at_then_taken_once_with_its_token_intact()
    {
        var store       = Store();
        var handshakeId = Guid.NewGuid();
        var userId      = Guid.NewGuid();
        var result      = HandshakeResult.Linked(Identity, Token, [ConnectionDetailKeys.Number(ConnectionDetailKeys.GitHubFollowers, 9)], "");

        await store.ParkAsync(handshakeId, userId, ConnectionProvider.GITHUB, result, ConnectReturnKind.WEB, replace: true);

        var peeked = await store.PeekParkedAsync(handshakeId);
        var taken  = await store.ConsumeParkedAsync(handshakeId);
        var again  = await store.ConsumeParkedAsync(handshakeId);

        Assert.Multiple(() =>
        {
            Assert.That(peeked?.UserId, Is.EqualTo(userId));
            Assert.That(taken, Is.Not.Null);
            Assert.That(taken!.Identity, Is.EqualTo(Identity));
            Assert.That(taken.Details, Has.Count.EqualTo(1));
            Assert.That(taken.Replace, Is.True);
            Assert.That(taken.ReturnTo, Is.EqualTo(ConnectReturnKind.WEB));
            Assert.That(taken.SealedToken, Is.Not.Null.And.Not.EqualTo("gho_abc"), "the token waits sealed");
            Assert.That(store.OpenParkedToken(taken), Is.EqualTo(Token));
            Assert.That(again, Is.Null);
        });
    }

    [Test]
    public async Task A_result_without_a_token_parks_without_one()
    {
        var store       = Store();
        var handshakeId = Guid.NewGuid();

        await store.ParkAsync(handshakeId, Guid.NewGuid(), ConnectionProvider.STEAM, HandshakeResult.Linked(Identity, null, [], ""), ConnectReturnKind.DESKTOP, false);

        var parked = await store.ConsumeParkedAsync(handshakeId);

        Assert.Multiple(() =>
        {
            Assert.That(parked?.SealedToken, Is.Null);
            Assert.That(store.OpenParkedToken(parked!), Is.Null);
        });
    }

    [Test]
    public async Task Begin_is_budgeted_per_user_per_minute()
    {
        var options = Default();
        options.BeginConnectPerMinute = 2;

        var store = Store(options);
        var user  = Guid.NewGuid();

        Assert.Multiple(async () =>
        {
            Assert.That(await store.TryAcquireBeginAsync(user), Is.True);
            Assert.That(await store.TryAcquireBeginAsync(user), Is.True);
            Assert.That(await store.TryAcquireBeginAsync(user), Is.False);
            Assert.That(await store.TryAcquireBeginAsync(Guid.NewGuid()), Is.True, "another user has their own budget");
        });
    }

    [Test]
    public async Task A_manual_refresh_arms_a_cooldown_per_connection()
    {
        var store = Store();
        var user  = Guid.NewGuid();

        var open = Default();
        open.RefreshCooldown = TimeSpan.Zero;

        Assert.Multiple(async () =>
        {
            Assert.That(await store.TryAcquireRefreshAsync(user, ConnectionProvider.GITHUB), Is.True);
            Assert.That(await store.TryAcquireRefreshAsync(user, ConnectionProvider.GITHUB), Is.False);
            Assert.That(await store.TryAcquireRefreshAsync(user, ConnectionProvider.STEAM), Is.True, "another provider, another cooldown");
            Assert.That(await Store(open).TryAcquireRefreshAsync(user, ConnectionProvider.GITHUB), Is.True, "no cooldown configured");
        });
    }
}
