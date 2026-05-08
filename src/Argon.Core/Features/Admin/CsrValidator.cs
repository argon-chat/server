namespace Argon.Features.Admin;

using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

public enum CsrValidationError
{
    InvalidPemFormat,
    UnsupportedKeyType,
    KeyTooSmall
}

public static class CsrValidator
{
    private const int MinRsaKeySize = 2048;

    public static Either<CertificateRequest, CsrValidationError> Validate(string csrPem)
    {
        try
        {
            var csr = LoadCsr(csrPem);
            var publicKey = csr.PublicKey;

            if (publicKey.Oid?.Value == "1.2.840.113549.1.1.1") // RSA
            {
                var rsa = publicKey.GetRSAPublicKey();
                if (rsa is null)
                    return CsrValidationError.UnsupportedKeyType;
                if (rsa.KeySize < MinRsaKeySize)
                    return CsrValidationError.KeyTooSmall;
            }
            else if (publicKey.Oid?.Value == "1.2.840.10045.2.1") // EC
            {
                var ec = publicKey.GetECDsaPublicKey();
                if (ec is null)
                    return CsrValidationError.UnsupportedKeyType;
                if (ec.KeySize < 256)
                    return CsrValidationError.KeyTooSmall;
            }
            else
            {
                return CsrValidationError.UnsupportedKeyType;
            }

            return csr;
        }
        catch
        {
            return CsrValidationError.InvalidPemFormat;
        }
    }

    private static CertificateRequest LoadCsr(string pem)
        => CertificateRequest.LoadSigningRequestPem(pem, HashAlgorithmName.SHA384);
}
