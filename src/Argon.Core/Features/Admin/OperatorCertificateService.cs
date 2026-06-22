namespace Argon.Features.Admin;

using System.Security.Cryptography.X509Certificates;
using Argon.Entities;
using Argon.Features.Vault;

public record CertificateEnrollmentResult(
    Guid CertificateId,
    string CertificatePem,
    string CaChainPem,
    string SerialNumber,
    string Thumbprint,
    DateTimeOffset NotAfter);

public enum EnrollmentError
{
    OperatorNotFound,
    OperatorInactive,
    InvalidCsr,
    CsrKeyTooSmall,
    VaultSigningFailed
}

public interface IOperatorCertificateService
{
    Task<Either<CertificateEnrollmentResult, EnrollmentError>> EnrollCertificateAsync(
        Guid operatorId, string csrPem, string? deviceName = null, string? deviceSerialNumber = null);

    /// <summary>Revokes a single certificate by its id (soft-revoke; the record is preserved as history).</summary>
    Task RevokeCertificateAsync(Guid certificateId);
}

public sealed class OperatorCertificateService(
    ApplicationDbContext db,
    IVaultPkiService     pkiService,
    ILogger<OperatorCertificateService> logger)
    : IOperatorCertificateService
{
    public async Task<Either<CertificateEnrollmentResult, EnrollmentError>> EnrollCertificateAsync(
        Guid operatorId, string csrPem, string? deviceName = null, string? deviceSerialNumber = null)
    {
        var op = await db.Operators.FirstOrDefaultAsync(x => x.Id == operatorId && !x.IsDeleted);

        if (op is null)
            return EnrollmentError.OperatorNotFound;
        if (!op.IsActive)
            return EnrollmentError.OperatorInactive;

        var csrValidation = CsrValidator.Validate(csrPem);
        if (!csrValidation.IsSuccess)
        {
            return csrValidation.Error switch
            {
                CsrValidationError.KeyTooSmall => EnrollmentError.CsrKeyTooSmall,
                _                              => EnrollmentError.InvalidCsr
            };
        }

        SignedCertificateResult signed;
        try
        {
            signed = await pkiService.SignCsrAsync(csrPem, $"operator:{op.Email}");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Vault PKI signing failed for operator={OperatorId}", operatorId);
            return EnrollmentError.VaultSigningFailed;
        }

        // parse signed cert to extract metadata
        var cert = X509Certificate2.CreateFromPem(signed.CertificatePem);

        // Add a new certificate record. Existing certificates remain active —
        // an operator may carry multiple certificates (one per device).
        var certificate = new OperatorCertificateEntity
        {
            OperatorId         = op.Id,
            SerialNumber       = signed.SerialNumber,
            Thumbprint         = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)),
            Subject            = cert.Subject,
            NotBefore          = cert.NotBefore,
            NotAfter           = cert.NotAfter,
            DeviceName         = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim(),
            DeviceSerialNumber = string.IsNullOrWhiteSpace(deviceSerialNumber) ? null : deviceSerialNumber.Trim(),
        };

        db.OperatorCertificates.Add(certificate);
        await db.SaveChangesAsync();

        logger.LogInformation("Enrolled certificate {CertificateId} for operator={OperatorId}, serial={Serial}, device={Device}",
            certificate.Id, operatorId, signed.SerialNumber, certificate.DeviceSerialNumber);

        var caChain = string.Join("\n", signed.CaChainPem);
        if (string.IsNullOrEmpty(caChain))
            caChain = signed.IssuingCaPem;

        return new CertificateEnrollmentResult(
            certificate.Id,
            signed.CertificatePem,
            caChain,
            signed.SerialNumber,
            certificate.Thumbprint,
            cert.NotAfter);
    }

    public async Task RevokeCertificateAsync(Guid certificateId)
    {
        var cert = await db.OperatorCertificates.FirstOrDefaultAsync(x => x.Id == certificateId && !x.IsDeleted)
                   ?? throw new InvalidOperationException($"Certificate {certificateId} not found");

        if (cert.RevokedAt is not null)
            return;

        await pkiService.RevokeCertificateAsync(cert.SerialNumber);

        cert.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        logger.LogInformation("Revoked certificate {CertificateId} (serial={Serial}) for operator={OperatorId}",
            cert.Id, cert.SerialNumber, cert.OperatorId);
    }
}
