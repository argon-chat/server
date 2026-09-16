namespace ArgonSharedLogicTest;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argon.Features.WebSession;

/// <summary>
/// The signature check behind device-bound sessions, exercised with real P-256 keys.
/// </summary>
/// <remarks>
/// <para>This is the part of DBSC that can be tested without a browser, and it is the part that
/// matters: everything else in the feature is plumbing around the answer this gives. The browser
/// half — whether Chromium likes our registration header, whether the refresh dance completes —
/// cannot be reached from here at all, so what is checked is that a proof only verifies when it
/// should.</para>
///
/// <para>The negatives are the point. A verifier that accepts everything passes the happy path just
/// as well as a correct one, and the failure would be a session bound to a key nobody holds.</para>
/// </remarks>
[TestFixture]
public class DeviceBoundProofTests
{
    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Builds the JWK a browser puts in its registration proof.</summary>
    private static string Jwk(ECDsa key)
    {
        var p = key.ExportParameters(false);
        return $$"""{"kty":"EC","crv":"P-256","x":"{{Base64Url(p.Q.X!)}}","y":"{{Base64Url(p.Q.Y!)}}"}""";
    }

    /// <summary>
    /// A <c>dbsc+jwt</c> as Chromium signs one.
    /// </summary>
    /// <remarks>
    /// The signature is IEEE P1363 — raw <c>r || s</c> — because that is what JWS <c>ES256</c> is.
    /// Signing it as a DER sequence here would make the test pass against a verifier that reads DER,
    /// and both would be wrong together.
    /// </remarks>
    private static string Sign(ECDsa key, string challenge, string? typ = "dbsc+jwt", bool withJwk = true)
    {
        var header = withJwk
            ? $$"""{"alg":"ES256","typ":{{(typ is null ? "null" : $"\"{typ}\"")}},"jwk":{{Jwk(key)}}}"""
            : $$"""{"alg":"ES256","typ":{{(typ is null ? "null" : $"\"{typ}\"")}}}""";

        var payload = $$"""{"jti":"{{challenge}}"}""";

        var signed = $"{Base64Url(Encoding.UTF8.GetBytes(header))}.{Base64Url(Encoding.UTF8.GetBytes(payload))}";

        var signature = key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{signed}.{Base64Url(signature)}";
    }

    [Test]
    public void A_registration_proof_verifies_and_names_its_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var identity = DeviceBoundProof.VerifyRegistration(Sign(key, "the-challenge"), "the-challenge");

        Assert.That(identity, Is.Not.Null);
        Assert.That(identity!.Thumbprint, Is.Not.Empty);
    }

    /// <summary>The same key always gets the same name, or a bound session becomes unreachable.</summary>
    [Test]
    public void The_thumbprint_is_stable_for_a_key_and_unique_between_keys()
    {
        using var one = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var two = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var first  = DeviceBoundProof.VerifyRegistration(Sign(one, "c"), "c")!.Thumbprint;
        var again  = DeviceBoundProof.VerifyRegistration(Sign(one, "c"), "c")!.Thumbprint;
        var other  = DeviceBoundProof.VerifyRegistration(Sign(two, "c"), "c")!.Thumbprint;

        Assert.Multiple(() =>
        {
            Assert.That(again, Is.EqualTo(first), "the same key was given two different names");
            Assert.That(other, Is.Not.EqualTo(first), "two different keys were given the same name");
        });
    }

    [Test]
    public void A_proof_answering_a_different_challenge_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.That(DeviceBoundProof.VerifyRegistration(Sign(key, "theirs"), "ours"), Is.Null,
            "a proof made for another challenge was accepted, so a captured one could be replayed");
    }

    [Test]
    public void A_token_that_is_not_a_dbsc_proof_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.That(DeviceBoundProof.VerifyRegistration(Sign(key, "c", typ: "JWT"), "c"), Is.Null,
            "a token minted for something else on this origin was accepted as a device proof");
    }

    [Test]
    public void A_tampered_payload_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var parts = Sign(key, "c").Split('.');
        var swapped = $"{parts[0]}.{Base64Url(Encoding.UTF8.GetBytes("""{"jti":"c","extra":"x"}"""))}.{parts[2]}";

        Assert.That(DeviceBoundProof.VerifyRegistration(swapped, "c"), Is.Null);
    }

    [Test]
    public void Nonsense_is_refused_rather_than_thrown_at_the_caller()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DeviceBoundProof.VerifyRegistration("", "c"), Is.Null);
            Assert.That(DeviceBoundProof.VerifyRegistration("a.b", "c"), Is.Null);
            Assert.That(DeviceBoundProof.VerifyRegistration("!!.??.$$", "c"), Is.Null);
            Assert.That(DeviceBoundProof.VerifyRefresh("not a token", "{}", "c"), Is.False);
        });
    }

    // ── refresh ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void A_refresh_verifies_against_the_key_the_session_was_registered_with()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var stored = DeviceBoundProof.VerifyRegistration(Sign(key, "first"), "first")!.PublicKeyJwk;

        Assert.That(DeviceBoundProof.VerifyRefresh(Sign(key, "second", withJwk: false), stored, "second"),
            Is.True);
    }

    /// <summary>
    /// The one that matters: a refresh must not be satisfied by a key it brought along itself.
    /// </summary>
    /// <remarks>
    /// A verifier that read the key out of the token would accept this, because the proof is
    /// perfectly valid — for the wrong key. The session would then be bound to whoever asked last,
    /// which is the same as being bound to nobody.
    /// </remarks>
    [Test]
    public void A_refresh_signed_by_another_key_is_refused_even_though_it_is_self_consistent()
    {
        using var registered = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker   = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var stored = DeviceBoundProof.VerifyRegistration(Sign(registered, "first"), "first")!.PublicKeyJwk;

        Assert.Multiple(() =>
        {
            Assert.That(DeviceBoundProof.VerifyRefresh(Sign(attacker, "second", withJwk: false), stored, "second"),
                Is.False, "a refresh signed by a key the session was never bound to was accepted");

            Assert.That(DeviceBoundProof.VerifyRefresh(Sign(attacker, "second"), stored, "second"),
                Is.False, "a refresh was accepted on the strength of a key it carried itself");
        });
    }

    [Test]
    public void The_stored_key_is_valid_json_and_round_trips()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var stored = DeviceBoundProof.VerifyRegistration(Sign(key, "c"), "c")!.PublicKeyJwk;
        var parsed = JsonDocument.Parse(stored).RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(parsed.GetProperty("kty").GetString(), Is.EqualTo("EC"));
            Assert.That(parsed.GetProperty("crv").GetString(), Is.EqualTo("P-256"));
            Assert.That(DeviceBoundProof.Thumbprint(parsed), Is.Not.Empty);
        });
    }
}
