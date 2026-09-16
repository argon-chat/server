namespace Argon.Features.WebSession;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// What a verified DBSC proof told us.
/// </summary>
/// <param name="Thumbprint">
/// RFC 7638 thumbprint of the key that signed it — the name a bound session is stored under. The key
/// itself is kept too, because a refresh has to be checked against it; the thumbprint is what travels
/// in logs and identifies a device without carrying its key around.
/// </param>
/// <param name="PublicKeyJwk">The JWK as it arrived, canonicalised, for storing and re-importing.</param>
public sealed record DeviceBoundIdentity(string Thumbprint, string PublicKeyJwk);

/// <summary>
/// Checks the JWT a browser signs with its device-bound key.
/// </summary>
/// <remarks>
/// <para><b>Deliberately not <see cref="Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler"/>.</b>
/// That validates a token against keys the server already trusts, and this is the opposite shape: on
/// registration the key arrives <i>inside</i> the token and the signature proves only that whoever
/// sent it holds the private half. There is no issuer, no audience and no expiry to check — the
/// challenge is what makes it fresh, and the challenge is ours. Handing that to a general-purpose
/// validator means configuring away almost everything it does and hoping the remainder is what was
/// wanted.</para>
///
/// <para><b>P-256 only, and the signature is raw.</b> JWS <c>ES256</c> is
/// <c>r || s</c>, fixed width — not the DER sequence <c>DeviceProofVerifier</c> reads from the
/// desktop's TPM. The two mechanisms are cousins and this is the one line where they differ; getting
/// it wrong produces a signature that never verifies and an error that says nothing.</para>
/// </remarks>
public static class DeviceBoundProof
{
    /// <summary>The only type this accepts. A token that does not say so is not a DBSC proof.</summary>
    public const string TokenType = "dbsc+jwt";

    /// <summary>
    /// Verifies a registration proof, whose key travels in its own header.
    /// </summary>
    /// <remarks>
    /// Self-signed by definition: the browser has just made this key and nothing has seen it before,
    /// so the signature attests possession and nothing else. What makes the exchange meaningful is
    /// <paramref name="expectedChallenge"/> — a value this server issued moments ago and will not
    /// accept twice.
    /// </remarks>
    public static DeviceBoundIdentity? VerifyRegistration(string jwt, string expectedChallenge)
    {
        if (!Split(jwt, out var header, out var payload, out var signature, out var signed))
            return null;

        if (!IsDbscHeader(header, out var jwk))
            return null;

        if (Import(jwk) is not { } key)
            return null;

        using (key)
        {
            if (!key.VerifyData(signed, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return null;
        }

        return ChallengeMatches(payload, expectedChallenge)
            ? new DeviceBoundIdentity(Thumbprint(jwk), Canonical(jwk))
            : null;
    }

    /// <summary>
    /// Verifies a refresh proof against the key the session was registered with.
    /// </summary>
    /// <remarks>
    /// The key is <b>not</b> read out of the token here, and that is the whole difference between
    /// this and registration. A refresh that trusted the key it carried would be satisfied by any
    /// key at all — which is to say by anyone — and the session would be bound to nothing.
    /// </remarks>
    public static bool VerifyRefresh(string jwt, string storedJwk, string expectedChallenge)
    {
        if (!Split(jwt, out var header, out var payload, out var signature, out var signed))
            return false;

        if (!IsDbscHeader(header, out _))
            return false;

        using var key = Import(storedJwk);

        if (key is null)
            return false;

        return key.VerifyData(signed, signature, HashAlgorithmName.SHA256,
                   DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            && ChallengeMatches(payload, expectedChallenge);
    }

    /// <summary>
    /// RFC 7638: SHA-256 over the required members, lexicographic, no whitespace.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than serialised from a model, because the point of a thumbprint is that two
    /// implementations agree on the bytes. A serialiser that reorders members or pads them produces a
    /// different name for the same key, and the session it names is then unreachable.
    /// </remarks>
    public static string Thumbprint(JsonElement jwk)
    {
        var crv = jwk.GetProperty("crv").GetString();
        var x   = jwk.GetProperty("x").GetString();
        var y   = jwk.GetProperty("y").GetString();

        var canonical = $$"""{"crv":"{{crv}}","kty":"EC","x":"{{x}}","y":"{{y}}"}""";

        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────

    private static bool Split(string jwt, out JsonElement header, out JsonElement payload,
        out byte[] signature, out byte[] signed)
    {
        header    = default;
        payload   = default;
        signature = [];
        signed    = [];

        var parts = jwt.Split('.');

        if (parts.Length != 3)
            return false;

        try
        {
            header    = JsonDocument.Parse(FromBase64Url(parts[0])).RootElement.Clone();
            payload   = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement.Clone();
            signature = FromBase64Url(parts[2]);
            signed    = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");

            return true;
        }
        catch (Exception)
        {
            // Every byte came from the caller. A token that will not parse is a token that failed,
            // not a fault worth propagating into the auth path.
            return false;
        }
    }

    private static bool IsDbscHeader(JsonElement header, out JsonElement jwk)
    {
        jwk = default;

        if (header.TryGetProperty("alg", out var alg) && alg.GetString() != "ES256")
            return false;

        // Checked because it is what stops a token minted for something else on this origin from
        // being replayed here as a device proof.
        if (!header.TryGetProperty("typ", out var typ) || typ.GetString() != TokenType)
            return false;

        if (header.TryGetProperty("jwk", out var carried))
            jwk = carried;

        return true;
    }

    private static bool ChallengeMatches(JsonElement payload, string expected)
        => payload.TryGetProperty("jti", out var jti)
        && jti.GetString() is { } presented
        && CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));

    private static ECDsa? Import(JsonElement jwk)
    {
        try
        {
            if (jwk.ValueKind != JsonValueKind.Object) return null;
            if (jwk.GetProperty("kty").GetString() != "EC") return null;
            if (jwk.GetProperty("crv").GetString() != "P-256") return null;

            return ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = FromBase64Url(jwk.GetProperty("x").GetString()!),
                    Y = FromBase64Url(jwk.GetProperty("y").GetString()!)
                }
            });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ECDsa? Import(string jwkJson)
    {
        try
        {
            return Import(JsonDocument.Parse(jwkJson).RootElement);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Canonical(JsonElement jwk)
        => $$"""{"crv":"{{jwk.GetProperty("crv").GetString()}}","kty":"EC","x":"{{jwk.GetProperty("x").GetString()}}","y":"{{jwk.GetProperty("y").GetString()}}"}""";

    internal static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    internal static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The session configuration a browser is handed after registering, and on every refresh.</summary>
/// <remarks>
/// Field names are the wire's, not ours — the browser reads this, and a member renamed to read better
/// in C# is a member it will not find. See the DBSC specification for what each one does.
/// </remarks>
public sealed record DeviceBoundSessionConfig(
    [property: JsonPropertyName("session_identifier")] string SessionIdentifier,
    [property: JsonPropertyName("refresh_url")] string RefreshUrl,
    [property: JsonPropertyName("scope")] DeviceBoundScope Scope,
    [property: JsonPropertyName("credentials")] IReadOnlyList<DeviceBoundCredential> Credentials);

public sealed record DeviceBoundScope(
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("include_site")] bool IncludeSite);

public sealed record DeviceBoundCredential(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("attributes")] string Attributes);
