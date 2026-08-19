namespace Argon.Grains;

using System.Security.Cryptography.X509Certificates;
using Argon.Entities;
using Argon.Features.Vault;
using Argon.Grains.Interfaces;

/// <summary>
/// Grain keyed by certificate thumbprint (hex SHA256).
/// Turns an operator's PIV certificate into an operator identity.
/// </summary>
/// <remarks>
/// Two ways in, and they differ only in how possession of the private key was proved. The admin
/// console signs a challenge this grain issued; the identity server takes the certificate straight
/// out of a mutual-TLS handshake that already did the proving. Everything after that — chain of
/// trust, the operator record behind the thumbprint, whether they are still active — is the same
/// work, and lives in <see cref="ResolveOperator"/> so the two paths cannot drift apart.
/// </remarks>
public class OperatorAuthChallengeGrain(
    IServiceProvider provider,
    ILogger<OperatorAuthChallengeGrain> logger)
    : Grain, IOperatorAuthChallengeGrain
{
    private readonly Dictionary<string, (byte[] Challenge, DateTime ExpiresAt)> pendingChallenges = new();
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    public Task<OperatorChallengeData> CreateChallenge()
    {
        CleanupExpired();

        var challengeId    = Guid.NewGuid().ToString("N");
        var challengeBytes = RandomNumberGenerator.GetBytes(32);
        pendingChallenges[challengeId] = (challengeBytes, DateTime.UtcNow + ChallengeLifetime);

        return Task.FromResult(new OperatorChallengeData(challengeId, challengeBytes));
    }

    public async Task<Either<OperatorAuthSuccess, OperatorAuthError>> VerifyChallenge(
        string challengeId, byte[] signature, byte[] certificateDer)
    {
        CleanupExpired();

        // 1. find and consume the challenge (one-time use)
        if (!pendingChallenges.Remove(challengeId, out var entry))
            return OperatorAuthError.ChallengeNotFound;

        if (DateTime.UtcNow > entry.ExpiresAt)
            return OperatorAuthError.ChallengeExpired;

        // 2. parse the client certificate
        X509Certificate2 cert;
        try
        {
            cert = X509CertificateLoader.LoadCertificate(certificateDer);
        }
        catch
        {
            return OperatorAuthError.InvalidSignature;
        }

        // 3. verify the signature over the challenge bytes
        if (!VerifySignature(cert, entry.Challenge, signature))
            return OperatorAuthError.InvalidSignature;

        return await ResolveOperator(cert, expectedUserId: null);
    }

    public async Task<Either<OperatorAuthSuccess, OperatorAuthError>> VerifyMutualTlsCertificate(
        byte[] certificateDer, Guid userId)
    {
        X509Certificate2 cert;
        try
        {
            cert = X509CertificateLoader.LoadCertificate(certificateDer);
        }
        catch
        {
            // Not a certificate at all. There is no signature in this path, so this is the closest
            // thing to "what you presented is not usable".
            return OperatorAuthError.InvalidSignature;
        }

        return await ResolveOperator(cert, userId);
    }

    /// <summary>
    /// Everything that holds whichever way the certificate arrived: it chains to our CA, an operator
    /// holds it, they are active, and — where the caller has a session to compare against — it is
    /// theirs.
    /// </summary>
    private async Task<Either<OperatorAuthSuccess, OperatorAuthError>> ResolveOperator(
        X509Certificate2 cert, Guid? expectedUserId)
    {
        await using var scope      = provider.CreateAsyncScope();
        var             pkiService = scope.ServiceProvider.GetRequiredService<IVaultPkiService>();

        var caPem = await pkiService.GetCaCertificateAsync();
        if (!VerifyChainOfTrust(cert, caPem))
            return OperatorAuthError.CertificateNotTrusted;

        var thumbprint = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var certificate = await db.OperatorCertificates
           .Include(c => c.Operator)
           .FirstOrDefaultAsync(c => c.Thumbprint == thumbprint && c.RevokedAt == null && !c.IsDeleted);

        var op = certificate?.Operator;
        if (certificate is null || op is null || op.IsDeleted)
        {
            logger.LogWarning("Operator certificate not found / not active for thumbprint {Thumbprint}", thumbprint);
            return OperatorAuthError.OperatorNotFound;
        }
        if (!op.IsActive)
        {
            logger.LogWarning("Operator {OperatorId} is inactive (cert {CertificateId})", op.Id, certificate.Id);
            return OperatorAuthError.OperatorInactive;
        }

        // A valid operator certificate is still the wrong one if it belongs to somebody else: the
        // handshake proves who holds the key, not who is signed in on this session.
        if (expectedUserId is { } userId && op.UserId != userId)
        {
            logger.LogWarning("Certificate operator {OperatorId} does not match session user {UserId}", op.Id, userId);
            return OperatorAuthError.CertificateUserMismatch;
        }

        // ⚠️ TEMPORARY WORKAROUND — Vault CRL revocation check is DISABLED.
        // FIXME(operator-cert-revocation / Vault): the `operator` PKI role (mount `pki-admin`) has
        // generate_lease=true with a ~24h mount lease TTL, so Vault auto-revokes every operator cert
        // ~24h after enrollment (lease expiry), even though the cert is valid for a year. Fix in Vault
        // (`vault patch pki-admin/roles/operator generate_lease=false`), then flip this flag back to true.
        var revocationCheckEnabled = false;
        if (revocationCheckEnabled)
        {
            using var caCert = X509Certificate2.CreateFromPem(caPem);
            var isRevoked = await pkiService.IsCertificateRevokedAsync(cert, caCert);
            if (isRevoked)
            {
                logger.LogWarning("Operator certificate revoked in Vault CRL — operator {OperatorId}, certId {CertificateId}, serial {Serial}, thumbprint {Thumbprint}",
                    op.Id, certificate.Id, certificate.SerialNumber, thumbprint);

                // Reconcile: revoked in Vault but still active in the DB — mark it revoked.
                certificate.RevokedAt = DateTimeOffset.UtcNow;
                try { await db.SaveChangesAsync(); }
                catch (Exception saveEx) { logger.LogError(saveEx, "Failed to reconcile RevokedAt for cert {CertificateId}", certificate.Id); }

                return OperatorAuthError.CertificateRevoked;
            }
        }
        else
        {
            logger.LogWarning("Vault CRL revocation check TEMPORARILY DISABLED (Vault generate_lease bug) — operator {OperatorId}, certId {CertificateId} authenticated without revocation verification",
                op.Id, certificate.Id);
        }

        op.LastAuthAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        logger.LogInformation("Operator {OperatorId} authenticated via PIV certificate", op.Id);

        return new OperatorAuthSuccess(op.Id, op.Email, thumbprint, op.DisplayName, op.IsSystemOperator);
    }

    private static bool VerifySignature(X509Certificate2 cert, byte[] data, byte[] signature)
    {
        var publicKey = cert.PublicKey;

        if (publicKey.GetECDsaPublicKey() is { } ecdsa)
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);

        if (publicKey.GetRSAPublicKey() is { } rsa)
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return false;
    }

    private static bool VerifyChainOfTrust(X509Certificate2 cert, string caPem)
    {
        using var caCert = X509Certificate2.CreateFromPem(caPem);
        using var chain  = new X509Chain();

        chain.ChainPolicy.RevocationMode    = X509RevocationMode.NoCheck; // we check via Vault
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.TrustMode         = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCert);

        return chain.Build(cert);
    }

    private void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        var expired = pendingChallenges
            .Where(x => now > x.Value.ExpiresAt)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in expired)
            pendingChallenges.Remove(key);
    }
}
