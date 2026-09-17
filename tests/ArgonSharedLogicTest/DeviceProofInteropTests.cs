namespace ArgonSharedLogicTest;

using Argon.Features.WebSession;

/// <summary>
/// That a proof produced by a browser's WebCrypto verifies here.
/// </summary>
/// <remarks>
/// <para><b>Why a recorded vector rather than a round trip.</b> Everything else in the suite signs
/// with .NET and verifies with .NET, which agrees with itself by construction. The thing that can
/// actually break is the seam between two languages: the driver in Firefox and Safari builds these
/// tokens with <c>crypto.subtle</c>, and if the two sides disagree about any of the format the only
/// symptom is a proof that never verifies — in browsers no test here runs, leaving sessions quietly
/// unbound.</para>
///
/// <para>The token below was produced by WebCrypto, not written by hand. It pins the four things
/// the two implementations have to agree on: base64url without padding, <c>typ: dbsc+jwt</c>, the
/// challenge in <c>jti</c>, and an ES256 signature as raw <c>r || s</c> rather than the DER the
/// desktop's TPM path produces. Regenerate it with <c>crypto.subtle</c>, never by editing it.</para>
/// </remarks>
[TestFixture]
public class DeviceProofInteropTests
{
    private const string Challenge = "vector-challenge-2f9c";

    /// <summary>Signed by WebCrypto with a P-256 key; the public half travels in the header.</summary>
    private const string Jwt =
        "eyJhbGciOiJFUzI1NiIsInR5cCI6ImRic2Mrand0IiwiandrIjp7Imt0eSI6IkVDIiwiY3J2IjoiUC0yNTYiLCJ4IjoiWHlRV0t4c04"
      + "xanZKRHNuVHdkT0JWUHhQNktseWNkSmNrTUVPX3R3ajNjWSIsInkiOiJ5c1VldE42TXd5cmVxMVN0V01XMXdHMnlpY1ZMdklidXdwM2"
      + "VDSGRISk40In19.eyJqdGkiOiJ2ZWN0b3ItY2hhbGxlbmdlLTJmOWMifQ.P4nBnKBQZzzzcGObw3hqtjRPLQMEKtvkozTTk6en6JG7H"
      + "hGOI_seG5dfUnU8hgtbwDL_osZXecejZVSW-GatUA";

    private const string Jwk =
        """{"kty":"EC","crv":"P-256","x":"XyQWKxsN1jvJDsnTwdOBVPxP6KlycdJckMEO_twj3cY","y":"ysUetN6Mwyreq1StWMW1wG2yicVLvIbuwp3eCHdHJN4"}""";

    [Test]
    public void VerifiesARegistrationProofMadeByWebCrypto()
        => Assert.That(DeviceBoundProof.VerifyRegistration(Jwt, Challenge), Is.Not.Null);

    [Test]
    public void VerifiesARefreshProofAgainstTheStoredKey()
        => Assert.That(DeviceBoundProof.VerifyRefresh(Jwt, Jwk, Challenge), Is.True);

    /// <summary>The challenge is what makes a proof fresh, so the wrong one must not pass.</summary>
    [Test]
    public void RejectsTheSameProofAgainstAnotherChallenge()
        => Assert.That(DeviceBoundProof.VerifyRegistration(Jwt, "some-other-challenge"), Is.Null);

    /// <summary>The key in the header is the one the thumbprint names, and it is stable.</summary>
    [Test]
    public void NamesTheSameKeyTheBrowserSent()
    {
        var identity = DeviceBoundProof.VerifyRegistration(Jwt, Challenge);

        Assert.That(identity, Is.Not.Null);
        Assert.That(identity!.PublicKeyJwk, Does.Contain("XyQWKxsN1jvJDsnTwdOBVPxP6KlycdJckMEO_twj3cY"));
        Assert.That(identity.Thumbprint, Is.Not.Empty);
    }

    /// <summary>A tampered payload must not verify, which is the signature doing its job.</summary>
    [Test]
    public void RejectsAProofWhosePayloadWasChanged()
    {
        var parts = Jwt.Split('.');
        var forged = $"{parts[0]}.{Convert.ToBase64String("{\"jti\":\"forged\"}"u8).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.{parts[2]}";

        Assert.That(DeviceBoundProof.VerifyRegistration(forged, "forged"), Is.Null);
    }
}
