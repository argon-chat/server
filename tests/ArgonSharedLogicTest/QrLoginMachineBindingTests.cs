namespace ArgonSharedLogicTest;

using System.Collections.Concurrent;
using Argon.Features.Auth;
using Argon.Services;
using Argon.Services.Ion;
using ArgonContracts;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The one property that makes a code on a screen safe to look at: it only pays out to the machine
/// that asked for it.
/// </summary>
/// <remarks>
/// <para>This lives here rather than beside the other QR tests in <c>ArgonComplexTest</c> because it
/// cannot be observed through a running host. <c>HttpContextExtensions.GetMachineId</c> returns the
/// constant <c>"1234"</c> whenever <c>IHostEnvironment.IsDevelopment()</c>, so every caller against
/// a development host — including two deliberately different test clients — presents the same
/// machine. An integration test written against that would pass while asserting nothing, which is
/// worse than no test.</para>
///
/// <para>Worth knowing beyond this file: the same short-circuit means the binding protects nothing
/// on any deployment running in Development. It is the production path that this test pins.</para>
/// </remarks>
[TestFixture]
public class QrLoginMachineBindingTests
{
    private const string Desktop   = "machine-of-the-desktop";
    private const string Bystander = "machine-of-whoever-photographed-the-screen";

    /// <summary>
    /// Enough of the cache for the paths under test: string get/set/delete and the counter pair the
    /// rate limiter uses. Expiry is not modelled — no test here turns the clock.
    /// </summary>
    private sealed class InMemoryCache : IArgonCacheDatabase
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public Task StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default)
            => StringSetAsync(key, value, ct);

        public Task StringSetAsync(string key, string value, CancellationToken ct = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> StringGetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task KeyDeleteAsync(string key, CancellationToken ct = default)
        {
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> KeyExistsAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_values.ContainsKey(key));

        public Task UpdateStringExpirationAsync(string key, TimeSpan expiration, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<long> StringIncrementAsync(string key, CancellationToken ct = default)
        {
            var next = long.Parse(_values.AddOrUpdate(
                key,
                "1",
                (_, current) => (long.Parse(current) + 1).ToString()));

            return Task.FromResult(next);
        }

        public Task<string> KeyExpireAsync(string key, TimeSpan window, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public async IAsyncEnumerable<string> ScanKeysAsync(
            string pattern,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var key in _values.Keys)
                yield return key;

            await Task.CompletedTask;
        }

        public Task<bool>     SetAddAsync(string key, string member, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool>     SetRemoveAsync(string key, string member, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string[]> SetMembersAsync(string key, CancellationToken ct = default) => Task.FromResult(Array.Empty<string>());
    }

    /// <summary>
    /// Puts a request context in place for the duration of one call, standing in for what the
    /// transaction interceptor builds per request.
    /// </summary>
    private static void ActAs(string machineId)
        => ArgonRequestContext.Set(new ArgonRequestContextData
        {
            Ip         = "203.0.113.7",
            Region     = "RU",
            Ray        = "test",
            ClientName = "Argon/test",
            AppId      = "argon.app",
            SessionId  = Guid.NewGuid(),
            MachineId  = machineId,
            UserId     = null,
            Scope      = null!,
        });

    /// <summary>
    /// <c>UserManagerService</c> is only reached by an approval, and nothing below approves — the
    /// machine is checked before the status is even looked at. Passing null keeps the fixture free
    /// of a database.
    /// </summary>
    private static QrLoginService CreateService(IArgonCacheDatabase cache)
        => new(cache, null!, NullLogger<QrLoginService>.Instance);

    [TearDown]
    public void ClearContext() => ArgonRequestContext.Set(null!);

    [Test]
    public async Task Poll_FromTheMachineThatAsked_IsAnswered()
    {
        var service = CreateService(new InMemoryCache());

        ActAs(Desktop);
        var created = await service.CreateAsync();
        var ticket  = ((SuccessCreateLoginRequest)created).ticket;

        ActAs(Desktop);
        var polled = await service.PollAsync(ticket.token);

        Assert.That(polled, Is.InstanceOf<PendingLoginRequest>());
    }

    [Test]
    public async Task Poll_FromAnotherMachine_IsRefused()
    {
        var service = CreateService(new InMemoryCache());

        ActAs(Desktop);
        var created = await service.CreateAsync();
        var ticket  = ((SuccessCreateLoginRequest)created).ticket;

        ActAs(Bystander);
        var stolen = await service.PollAsync(ticket.token);

        Assert.That(stolen, Is.InstanceOf<FailedLoginPoll>());
        Assert.That(((FailedLoginPoll)stolen).error, Is.EqualTo(LoginRequestError.DEVICE_MISMATCH));
    }

    [Test]
    public async Task Poll_FromAnotherMachine_LeavesTheRequestForItsOwner()
    {
        var cache   = new InMemoryCache();
        var service = CreateService(cache);

        ActAs(Desktop);
        var created = await service.CreateAsync();
        var ticket  = ((SuccessCreateLoginRequest)created).ticket;

        ActAs(Bystander);
        await service.PollAsync(ticket.token);

        // A refusal that also burnt the record would let a bystander cancel someone else's sign-in
        // just by polling — a denial of service in place of a theft.
        ActAs(Desktop);
        var polled = await service.PollAsync(ticket.token);

        Assert.That(polled, Is.InstanceOf<PendingLoginRequest>());
    }

    [Test]
    public async Task Create_WithoutAMachine_IsRefused()
    {
        var service = CreateService(new InMemoryCache());

        ActAs(null!);

        // Nothing to bind the code to means nothing to check at collection time, so the request is
        // refused rather than issued unbound.
        var created = await service.CreateAsync();

        Assert.That(created, Is.InstanceOf<FailedCreateLoginRequest>());
        Assert.That(((FailedCreateLoginRequest)created).error, Is.EqualTo(LoginRequestError.INTERNAL_ERROR));
    }
}
