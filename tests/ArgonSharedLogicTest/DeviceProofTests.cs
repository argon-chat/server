namespace ArgonSharedLogicTest;

using System.Security.Cryptography;
using Argon.Features.Auth;

/// <summary>
/// The proof of possession that native code pushes into the <c>ArgonSecure</c> cookie.
/// </summary>
/// <remarks>
/// <para>Everything else in that cookie — <c>colt</c>, the fingerprint vector — is a value the client
/// computes and could as easily invent. This is the field that cannot be: only the holder of a
/// private key that never leaves its hardware can produce the signature.</para>
///
/// <para>There is no server nonce, because there is no service to ask for one; hardware identity
/// deliberately has no place in the ion contract. Freshness comes from the timestamp instead, which
/// makes the window and the machine binding load-bearing rather than incidental — those are what the
/// tests below are about.</para>
///
/// <para>Real ECDSA throughout. A fake key would test the shape of the code rather than that the
/// verification is a verification.</para>
/// </remarks>
[TestFixture]
public class DeviceProofTests
{
    private const string Machine = "machine-abc";

    private static (string publicKey, ECDsa key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    private static DeviceProof Proof(ECDsa key, string publicKey, string machineId = Machine, long? at = null)
    {
        var issuedAt = at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var signature = Convert.ToBase64String(key.SignData(
            System.Text.Encoding.ASCII.GetBytes($"{issuedAt}|{machineId}"),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        return new DeviceProof(publicKey, issuedAt, signature, null);
    }

    [Test]
    public void AGenuineProof_Verifies()
    {
        var (publicKey, key) = NewKey();

        Assert.That(DeviceProofVerifier.VerifySignature(Proof(key, publicKey), Machine), Is.True);
    }

    [Test]
    public void AnotherKeysProof_DoesNot()
    {
        var (publicKey, _)  = NewKey();
        var (_, attacker)   = NewKey();

        // The mechanism in one assertion: holding the public key is not enough to answer for it.
        Assert.That(DeviceProofVerifier.VerifySignature(Proof(attacker, publicKey), Machine), Is.False);
    }

    [Test]
    public void AProofFromAnotherMachine_DoesNot()
    {
        var (publicKey, key) = NewKey();

        // The machine id is signed over rather than merely sent alongside. Without that, a proof
        // lifted out of one machine's cookie would verify perfectly in another's — which is what
        // colt already is, and the reason this field exists.
        var elsewhere = Proof(key, publicKey, machineId: "some-other-machine");

        Assert.That(DeviceProofVerifier.VerifySignature(elsewhere, Machine), Is.False);
    }

    [Test]
    public void AProofSignedForADifferentMoment_DoesNot()
    {
        var (publicKey, key) = NewKey();
        var genuine          = Proof(key, publicKey);

        // Same signature, relabelled with a fresh timestamp: the pair has to hold together, or the
        // window could be sidestepped by rewriting the number beside it.
        var relabelled = genuine with { IssuedAt = genuine.IssuedAt + 30 };

        Assert.That(DeviceProofVerifier.VerifySignature(relabelled, Machine), Is.False);
    }

    [TestCase("")]
    [TestCase("not base64 at all")]
    [TestCase("aGVsbG8=")]
    public void AMalformedSignature_IsRefusedRatherThanThrowing(string signature)
    {
        var (publicKey, _) = NewKey();

        // Every byte came from the caller, so this is a failed proof and not a fault to propagate.
        var proof = new DeviceProof(publicKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), signature, null);

        Assert.That(DeviceProofVerifier.VerifySignature(proof, Machine), Is.False);
    }

    [TestCase("")]
    [TestCase("garbage")]
    public void AMalformedPublicKey_IsRefused(string publicKey)
    {
        Assert.That(DeviceProofVerifier.IsAcceptablePublicKey(publicKey), Is.False);
    }

    [Test]
    public void AWrongCurve_IsRefusedBeforeItReachesStorage()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        // Not because P-384 is weak, but because all three hardware stores speak P-256 and a key
        // that is not one of theirs did not come from where it claims.
        Assert.That(
            DeviceProofVerifier.IsAcceptablePublicKey(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())),
            Is.False);
    }

    // ── Parsing the cookie field ────────────────────────────────────

    [Test]
    public void TheCookieField_RoundTrips()
    {
        var parsed = DeviceProofVerifier.Parse("pk.1700000000.sig");

        Assert.Multiple(() =>
        {
            Assert.That(parsed!.PublicKey, Is.EqualTo("pk"));
            Assert.That(parsed.IssuedAt, Is.EqualTo(1700000000));
            Assert.That(parsed.Signature, Is.EqualTo("sig"));
            Assert.That(parsed.Attestation, Is.Null);
        });
    }

    [Test]
    public void AnAttestationIsOptionalAndCarriedLast()
    {
        var parsed = DeviceProofVerifier.Parse("pk.1700000000.sig.chain");

        Assert.That(parsed!.Attestation, Is.EqualTo("chain"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("garbage")]
    [TestCase("pk.notanumber.sig")]
    [TestCase("pk.1700000000")]
    public void UnreadableInput_YieldsNothingRatherThanThrowing(string? raw)
    {
        // This parses on the auth path for every caller, including clients too old to send the field
        // at all. A malformed proof means we learn nothing about the machine, never a failed request.
        Assert.That(DeviceProofVerifier.Parse(raw), Is.Null);
    }

    [Test]
    public void AThumbprint_IsStableAndKeySpecific()
    {
        var (one, _) = NewKey();
        var (two, _) = NewKey();

        Assert.Multiple(() =>
        {
            Assert.That(DeviceProofVerifier.Thumbprint(one), Is.EqualTo(DeviceProofVerifier.Thumbprint(one)));
            Assert.That(DeviceProofVerifier.Thumbprint(one), Is.Not.EqualTo(DeviceProofVerifier.Thumbprint(two)));
            Assert.That(DeviceProofVerifier.Thumbprint(one), Does.Not.Contain("+").And.Not.Contain("/").And.Not.Contain("="));
        });
    }
}

/// <summary>
/// What the unattested verifier is allowed to conclude.
/// </summary>
/// <remarks>
/// The interesting case is not the happy one. A client that offers an attestation to a platform with
/// no way to check it must be refused, not quietly recorded as <c>KEY</c> — otherwise forging a blob
/// would land a caller on the same level as an honest client while looking like they tried harder,
/// which inverts the incentive the levels exist to create.
/// </remarks>
[TestFixture]
public class UnattestedVerifierTests
{
    [Test]
    public void NoBlob_IsTierOne()
    {
        var verdict = new UnattestedDeviceVerifier(DevicePlatform.LINUX).Verify("key", "nonce", null);

        Assert.That(verdict.Assurance, Is.EqualTo(DeviceAssurance.KEY));
    }

    [Test]
    public void AnUncheckableBlob_IsRefusedRatherThanDowngraded()
    {
        var verdict = new UnattestedDeviceVerifier(DevicePlatform.LINUX).Verify("key", "nonce", "pretend-attestation");

        Assert.That(verdict.Assurance, Is.EqualTo(DeviceAssurance.NONE));
    }
}
