namespace ArgonSharedLogicTest;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Argon.Features.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// What the Android verifier is willing to conclude, and — more to the point — what it is not.
/// </summary>
/// <remarks>
/// <para>A verifier that says ATTESTED too readily is worse than no verifier, because the level is
/// what a hardware ban and alt detection are later hung on. So the cases here are the refusals: a
/// self-signed chain, a chain for a different key, a chain that does not reach a pinned root.</para>
///
/// <para>A genuine Google chain cannot be produced in a test — it comes from a real device with a
/// real attestation key — so the positive path is verified on hardware, not here. That is the same
/// boundary the client sits behind: an emulator produces an unrooted chain too.</para>
/// </remarks>
[TestFixture]
public class AndroidAttestationTests
{
    /// <summary>
    /// A verifier pinned to the given roots, with no network in play.
    /// </summary>
    /// <remarks>
    /// Configured roots are an override that short-circuits the fetch, which is what makes these
    /// tests deterministic: none of them reaches Google, and passing none is how the
    /// "cannot judge" case is reproduced.
    /// </remarks>
    private static AndroidKeyAttestationVerifier Verifier(params string[] roots)
    {
        var options = Options.Create(new AndroidAttestationOptions { RootCertificatesPem = roots });

        return new AndroidKeyAttestationVerifier(
            options,
            new AndroidAttestationRoots(new NoNetworkHttpClientFactory(), options,
                NullLogger<AndroidAttestationRoots>.Instance),
            NullLogger<AndroidKeyAttestationVerifier>.Instance);
    }

    /// <summary>Hands out a client whose every request fails, so a test can never depend on Google.</summary>
    private sealed class NoNetworkHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FailingHandler());

        private sealed class FailingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => throw new HttpRequestException("no network in tests");
        }
    }

    private static (string publicKey, X509Certificate2 certificate) SelfSigned(string nonce = "unused")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest("CN=not-google", key, HashAlgorithmName.SHA256);
        var cert    = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), cert);
    }

    private static string Pem(X509Certificate2 certificate)
        => certificate.ExportCertificatePem();

    [Test]
    public void NoChainOffered_IsTierOneNotARefusal()
    {
        var verdict = Verifier(Pem(SelfSigned().certificate)).Verify("key", "nonce", null);

        // A client that never claimed to be attested has not done anything wrong.
        Assert.That(verdict.Assurance, Is.EqualTo(DeviceAssurance.KEY));
    }

    [Test]
    public void WithNoRootsConfigured_ItDowngradesRatherThanRefusing()
    {
        var (publicKey, certificate) = SelfSigned();

        var verdict = Verifier().Verify(publicKey, "nonce", Convert.ToBase64String(certificate.RawData));

        // An unconfigured deployment is an operator problem, and refusing would mean Android cannot
        // enrol at all until someone notices. Nobody can reach ATTESTED there anyway, so the key is
        // taken at face value and the log carries the complaint.
        Assert.That(verdict.Assurance, Is.EqualTo(DeviceAssurance.KEY));
    }

    [Test]
    public void ASelfSignedChain_DoesNotReachAttested()
    {
        var (publicKey, certificate) = SelfSigned();
        var someoneElsesRoot         = Pem(SelfSigned().certificate);

        var verdict = Verifier(someoneElsesRoot)
           .Verify(publicKey, "nonce", Convert.ToBase64String(certificate.RawData));

        // This is the case that decides whether ATTESTED means anything: anyone can mint a
        // certificate, so a chain is only worth something because of where it ends.
        Assert.That(verdict.Assurance, Is.EqualTo(DeviceAssurance.NONE));
    }

    [Test]
    public void AChainForADifferentKey_IsRefused()
    {
        var (_, certificate) = SelfSigned();
        var (otherKey, _)    = SelfSigned();

        var verdict = Verifier(Pem(SelfSigned().certificate))
           .Verify(otherKey, "nonce", Convert.ToBase64String(certificate.RawData));

        // Without this a caller could present a genuine chain for one key while registering another
        // one they hold in software.
        Assert.That(verdict.Assurance, Is.EqualTo(DeviceAssurance.NONE));
    }

    [Test]
    public void AMalformedChain_IsRefusedRatherThanThrowing()
    {
        var verdict = Verifier(Pem(SelfSigned().certificate)).Verify("key", "nonce", "not-base64-at-all");

        Assert.That(verdict.Assurance, Is.EqualTo(DeviceAssurance.NONE));
    }
}
