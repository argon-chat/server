namespace Argon.Grains;

using System.Security.Cryptography.X509Certificates;
using Argon.Entities;
using Argon.Features.Vault;
using Argon.Grains.Interfaces;

/// <summary>
/// Grain keyed by certificate thumbprint (hex SHA256).
/// Manages challenge-response authentication for operator PIV certificates.
/// </summary>
public class OperatorAuthChallengeGrain(
    IServiceProvider provider,
    ILogger<OperatorAuthChallengeGrain> logger)
    : Grain, IOperatorAuthChallengeGrain
{
    private readonly Dictionary<string, (byte[] Challenge, DateTime ExpiresAt)> _pendingChallenges = new();
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    public Task<OperatorChallengeData> CreateChallenge()
    {
        CleanupExpired();

        var challengeId    = Guid.NewGuid().ToString("N");
        var challengeBytes = RandomNumberGenerator.GetBytes(32);
        _pendingChallenges[challengeId] = (challengeBytes, DateTime.UtcNow + ChallengeLifetime);

        return Task.FromResult(new OperatorChallengeData(challengeId, challengeBytes));
    }

    public async Task<Either<OperatorAuthSuccess, OperatorAuthError>> VerifyChallenge(
        string challengeId, byte[] signature, byte[] certificateDer)
    {
        CleanupExpired();

        // 1. find and consume the challenge (one-time use)
        if (!_pendingChallenges.Remove(challengeId, out var entry))
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

        // 4. verify chain of trust against our CA
        await using var scope      = provider.CreateAsyncScope();
        var             pkiService = scope.ServiceProvider.GetRequiredService<IVaultPkiService>();

        var caPem = await pkiService.GetCaCertificateAsync();
        if (!VerifyChainOfTrust(cert, caPem))
            return OperatorAuthError.CertificateNotTrusted;

        // 5. check revocation
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

        // 6. update last auth timestamp
        op.LastAuthAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        logger.LogInformation("Operator {OperatorId} authenticated via PIV certificate", op.Id);

        return new OperatorAuthSuccess(op.Id, op.Email, thumbprint);
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
        var expired = _pendingChallenges
            .Where(x => now > x.Value.ExpiresAt)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in expired)
            _pendingChallenges.Remove(key);
    }
}
