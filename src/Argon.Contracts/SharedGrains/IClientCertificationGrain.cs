namespace Argon.Shared.SharedGrains;

[Alias("Argon.Shared.SharedGrains.IExternalClientCertificationGrain")]
public interface IExternalClientCertificationGrain : IGrainWithGuidKey
{
    [Alias(nameof(ValidateCertificateFingerprint))]
    Task<bool> ValidateCertificateFingerprint(string appId, string fingerprint, Guid sessionId, Guid userId, Guid machineId);
}