namespace Argon.Features.Admin;

using System.Security.Cryptography.X509Certificates;
using Argon.Entities;
using Argon.Features.Vault;

public record CertificateEnrollmentResult(
    string CertificatePem,
    string CaChainPem,
    string SerialNumber,
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
    Task<Either<CertificateEnrollmentResult, EnrollmentError>> EnrollCertificateAsync(Guid operatorId, string csrPem);
    Task RevokeCertificateAsync(Guid operatorId);
}

public sealed class OperatorCertificateService(
    ApplicationDbContext db,
    IVaultPkiService     pkiService,
    ILogger<OperatorCertificateService> logger)
    : IOperatorCertificateService
{
    public async Task<Either<CertificateEnrollmentResult, EnrollmentError>> EnrollCertificateAsync(Guid operatorId, string csrPem)
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

        // revoke existing certificate if any
        if (!string.IsNullOrEmpty(op.CertificateSerialNumber))
        {
            try
            {
                await pkiService.RevokeCertificateAsync(op.CertificateSerialNumber);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to revoke old certificate serial={Serial} for operator={OperatorId}",
                    op.CertificateSerialNumber, operatorId);
            }
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

        op.CertificateSerialNumber = signed.SerialNumber;
        op.CertificateThumbprint   = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
        op.CertificateSubject      = cert.Subject;
        op.CertificateNotBefore    = cert.NotBefore;
        op.CertificateNotAfter     = cert.NotAfter;

        await db.SaveChangesAsync();

        logger.LogInformation("Enrolled certificate for operator={OperatorId}, serial={Serial}",
            operatorId, signed.SerialNumber);

        var caChain = string.Join("\n", signed.CaChainPem);
        if (string.IsNullOrEmpty(caChain))
            caChain = signed.IssuingCaPem;

        return new CertificateEnrollmentResult(
            signed.CertificatePem,
            caChain,
            signed.SerialNumber,
            cert.NotAfter);
    }

    public async Task RevokeCertificateAsync(Guid operatorId)
    {
        var op = await db.Operators.FirstOrDefaultAsync(x => x.Id == operatorId && !x.IsDeleted)
                 ?? throw new InvalidOperationException($"Operator {operatorId} not found");

        if (!string.IsNullOrEmpty(op.CertificateSerialNumber))
        {
            await pkiService.RevokeCertificateAsync(op.CertificateSerialNumber);

            op.CertificateSerialNumber = null;
            op.CertificateThumbprint   = null;
            op.CertificateSubject      = null;
            op.CertificateNotBefore    = null;
            op.CertificateNotAfter     = null;

            await db.SaveChangesAsync();

            logger.LogInformation("Revoked certificate for operator={OperatorId}", operatorId);
        }
    }
}
