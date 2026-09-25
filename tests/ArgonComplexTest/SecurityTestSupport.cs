namespace ArgonComplexTest;

using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Argon.Features.Vault;
using Fido2NetLib;
using Fido2NetLib.Objects;

/// <summary>
/// The operator CA, for a suite that has no Vault.
/// </summary>
/// <remarks>
/// <para>The step-up grain asks <see cref="IVaultPkiService.GetCaCertificateAsync"/> for the CA every
/// operator certificate has to chain to, and without Vault that call throws before any of the checks
/// behind it run. So this answers it with a CA of its own and can issue leaf certificates under it.</para>
///
/// <para>Only that one call is answered here. Signing, revoking and the CRL go to
/// <see cref="FakeVaultPkiService"/>, the in-memory Vault the operator-console tests read back.</para>
/// </remarks>
public sealed class TestOperatorPki : IVaultPkiService, IDisposable
{
    private readonly FakeVaultPkiService vault;
    private readonly ECDsa           authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TestOperatorPki(FakeVaultPkiService vault)
    {
        this.vault = vault;

        var request = new CertificateRequest("CN=Argon Test Operator CA", authorityKey, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        Authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public X509Certificate2 Authority { get; }

    /// <summary>A leaf certificate for <paramref name="key"/>, signed by <see cref="Authority"/>.</summary>
    public X509Certificate2 Issue(string commonName, AsymmetricAlgorithm key)
    {
        var subject = new X500DistinguishedName($"CN={commonName}");

        var request = key switch
        {
            ECDsa ecdsa => new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256),
            RSA rsa     => new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _           => new CertificateRequest(subject, new PublicKey(key), HashAlgorithmName.SHA256)
        };

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.2")], false));

        return request.Create(
            Authority.SubjectName,
            X509SignatureGenerator.CreateForECDsa(authorityKey),
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(30),
            RandomNumberGenerator.GetBytes(16));
    }

    public Task<string> GetCaCertificateAsync()
        => Task.FromResult(Authority.ExportCertificatePem());

    public Task<SignedCertificateResult> SignCsrAsync(string csrPem, string commonName, TimeSpan? ttl = null)
        => vault.SignCsrAsync(csrPem, commonName, ttl);

    public Task RevokeCertificateAsync(string serialNumber)
        => vault.RevokeCertificateAsync(serialNumber);

    public Task<bool> IsCertificateRevokedAsync(X509Certificate2 certificate, X509Certificate2 issuerCertificate)
        => vault.IsCertificateRevokedAsync(certificate, issuerCertificate);

    public void Dispose()
    {
        Authority.Dispose();
        authorityKey.Dispose();
    }
}

/// <summary>
/// A WebAuthn authenticator in software: one ES256 credential, "none" attestation.
/// </summary>
/// <remarks>
/// <para>Produces the same JSON a browser hands the page after <c>navigator.credentials.create</c>
/// and <c>.get</c>, byte for byte in the parts the server verifies — the client data, the
/// authenticator data, the COSE key and the signature — so the server's Fido2 verification runs for
/// real rather than being stepped around by seeding rows.</para>
///
/// <para>The relying party is the one <c>OtpExtensions.AddOtpCodes</c> configures: <c>argon.gl</c>,
/// with <c>https://argon.gl</c> among its origins.</para>
/// </remarks>
public sealed class SoftwareAuthenticator : IDisposable
{
    public const string RelyingPartyId = "argon.gl";
    public const string Origin         = "https://argon.gl";

    private const byte UserPresent           = 0x01;
    private const byte UserVerified          = 0x04;
    private const byte AttestedCredentialData = 0x40;

    public SoftwareAuthenticator(Guid aaGuid)
    {
        AaGuid = aaGuid;
    }

    public ECDsa  Key          { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);
    public Guid   AaGuid       { get; }

    /// <summary>The counter the authenticator reports; bumped on every assertion.</summary>
    public uint SignCount { get; private set; }

    /// <summary>The user handle the relying party gave at registration, sent back with assertions.</summary>
    public byte[]? UserHandle { get; private set; }

    /// <summary>Answers a <c>PublicKeyCredentialCreationOptions</c> JSON with a registration response JSON.</summary>
    public string Register(string creationOptionsJson)
    {
        using var options = JsonDocument.Parse(creationOptionsJson);

        var challenge = options.RootElement.GetProperty("challenge").GetString()!;
        UserHandle = Base64UrlDecode(options.RootElement.GetProperty("user").GetProperty("id").GetString()!);

        var clientData = ClientData("webauthn.create", challenge);

        var parameters = Key.ExportParameters(false);
        var coseKey    = new CborWriter(CborConformanceMode.Ctap2Canonical);

        coseKey.WriteStartMap(5);
        coseKey.WriteInt32(1);
        coseKey.WriteInt32(2);   // kty: EC2
        coseKey.WriteInt32(3);
        coseKey.WriteInt32(-7);  // alg: ES256
        coseKey.WriteInt32(-1);
        coseKey.WriteInt32(1);   // crv: P-256
        coseKey.WriteInt32(-2);
        coseKey.WriteByteString(parameters.Q.X!);
        coseKey.WriteInt32(-3);
        coseKey.WriteByteString(parameters.Q.Y!);
        coseKey.WriteEndMap();

        var credentialIdLength = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(credentialIdLength, (ushort)CredentialId.Length);

        var authenticatorData = Concat(
            AuthenticatorDataHeader(UserPresent | UserVerified | AttestedCredentialData),
            AaGuid.ToByteArray(bigEndian: true),
            credentialIdLength,
            CredentialId,
            coseKey.Encode());

        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);

        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt");
        attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt");
        attestation.WriteStartMap(0);
        attestation.WriteEndMap();
        attestation.WriteTextString("authData");
        attestation.WriteByteString(authenticatorData);
        attestation.WriteEndMap();

        return JsonSerializer.Serialize(new AuthenticatorAttestationRawResponse
        {
            Id    = Base64UrlEncode(CredentialId),
            RawId = CredentialId,
            Type  = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                AttestationObject = attestation.Encode(),
                ClientDataJson    = clientData,
                Transports        = [AuthenticatorTransport.Internal]
            },
            ClientExtensionResults = new AuthenticationExtensionsClientOutputs()
        });
    }

    /// <summary>
    /// Answers a <c>PublicKeyCredentialRequestOptions</c> JSON with an assertion response JSON.
    /// </summary>
    /// <param name="signWith">A different key to sign with — a forged assertion.</param>
    /// <param name="credentialId">A different credential to claim — one the server never registered.</param>
    public string Assert(string requestOptionsJson, ECDsa? signWith = null, byte[]? credentialId = null)
    {
        using var options = JsonDocument.Parse(requestOptionsJson);

        var challenge  = options.RootElement.GetProperty("challenge").GetString()!;
        var clientData = ClientData("webauthn.get", challenge);

        SignCount++;

        var authenticatorData = AuthenticatorDataHeader(UserPresent | UserVerified);
        var signature = (signWith ?? Key).SignData(
            Concat(authenticatorData, SHA256.HashData(clientData)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        var id = credentialId ?? CredentialId;

        return JsonSerializer.Serialize(new AuthenticatorAssertionRawResponse
        {
            Id    = Base64UrlEncode(id),
            RawId = id,
            Type  = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = authenticatorData,
                Signature         = signature,
                ClientDataJson    = clientData,
                UserHandle        = UserHandle
            },
            ClientExtensionResults = new AuthenticationExtensionsClientOutputs()
        });
    }

    private byte[] AuthenticatorDataHeader(int flags)
    {
        var counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, SignCount);

        return Concat(SHA256.HashData(Encoding.UTF8.GetBytes(RelyingPartyId)), [(byte)flags], counter);
    }

    private static byte[] ClientData(string type, string challenge)
        => JsonSerializer.SerializeToUtf8Bytes(new { type, challenge, origin = Origin, crossOrigin = false });

    private static byte[] Concat(params byte[][] parts)
        => parts.SelectMany(p => p).ToArray();

    public static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    public void Dispose()
        => Key.Dispose();
}
