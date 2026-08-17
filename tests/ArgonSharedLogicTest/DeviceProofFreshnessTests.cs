namespace ArgonSharedLogicTest;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Argon.Features.Auth;
using Argon.Services;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The window and the replay guard, which are what stand in for a server-issued nonce.
/// </summary>
/// <remarks>
/// <para>Device identity rides the <c>ArgonSecure</c> cookie because that is what the cookie is for,
/// and that choice costs the challenge round trip a service would have given. Freshness therefore
/// rests entirely on these two rules: a proof is only valid for a minute, and only once.</para>
///
/// <para>Which makes them the interesting part to pin. If either lapses, the <c>dev</c> field decays
/// into a stored bearer value — copyable, exactly like <c>colt</c> — and the whole mechanism is
/// theatre.</para>
/// </remarks>
[TestFixture]
public class DeviceProofFreshnessTests
{
    private const string Machine = "machine-abc";

    private static DeviceProofVerifier NewVerifier()
        => new(new ProofCache(), NullLogger<DeviceProofVerifier>.Instance);

    private static DeviceProof Proof(ECDsa key, string publicKey, long issuedAt)
    {
        var signature = Convert.ToBase64String(key.SignData(
            System.Text.Encoding.ASCII.GetBytes($"{issuedAt}|{Machine}"),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        return new DeviceProof(publicKey, issuedAt, signature, null);
    }

    private static (string publicKey, ECDsa key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    [Test]
    public async Task AFreshProof_IsAccepted()
    {
        var (publicKey, key) = NewKey();

        var accepted = await NewVerifier().VerifyAsync(
            Proof(key, publicKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), Machine);

        Assert.That(accepted, Is.True);
    }

    [Test]
    public async Task TheSameProofTwice_IsRefusedTheSecondTime()
    {
        var (publicKey, key) = NewKey();
        var verifier         = NewVerifier();
        var proof            = Proof(key, publicKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        Assert.That(await verifier.VerifyAsync(proof, Machine), Is.True);

        // Without this the field is a stored bearer value: anyone who copied the cookie inside the
        // window could present it as their own.
        Assert.That(await verifier.VerifyAsync(proof, Machine), Is.False);
    }

    [Test]
    public async Task AStaleProof_IsRefused()
    {
        var (publicKey, key) = NewKey();
        var old              = DateTimeOffset.UtcNow.Add(-DeviceProofVerifier.Window).AddSeconds(-30).ToUnixTimeSeconds();

        Assert.That(await NewVerifier().VerifyAsync(Proof(key, publicKey, old), Machine), Is.False);
    }

    [Test]
    public async Task AProofFromTheFuture_IsRefused()
    {
        var (publicKey, key) = NewKey();
        var ahead            = DateTimeOffset.UtcNow.Add(DeviceProofVerifier.Window).AddSeconds(30).ToUnixTimeSeconds();

        // The window applies in both directions: a clock can be fast as easily as slow, and a proof
        // dated forward would otherwise stay valid for twice as long as intended.
        Assert.That(await NewVerifier().VerifyAsync(Proof(key, publicKey, ahead), Machine), Is.False);
    }

    [Test]
    public async Task AProofForAnotherMachine_IsRefused()
    {
        var (publicKey, key) = NewKey();
        var proof            = Proof(key, publicKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        Assert.That(await NewVerifier().VerifyAsync(proof, "some-other-machine"), Is.False);
    }

    [Test]
    public async Task AFailedProof_DoesNotConsumeTheReplaySlot()
    {
        var (publicKey, key) = NewKey();
        var verifier         = NewVerifier();
        var issuedAt         = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var proof            = Proof(key, publicKey, issuedAt);

        // Rejected for the wrong machine, so nothing about it was accepted.
        Assert.That(await verifier.VerifyAsync(proof, "elsewhere"), Is.False);

        // The genuine presentation must still work: recording a refusal would let an attacker burn
        // somebody else's proof by presenting it badly first.
        Assert.That(await verifier.VerifyAsync(proof, Machine), Is.True);
    }

    private sealed class ProofCache : IArgonCacheDatabase
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
}
