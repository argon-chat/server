namespace Argon.Grains.Interfaces;

public record OperatorChallengeData(string ChallengeId, byte[] ChallengeBytes);

public record OperatorAuthSuccess(Guid OperatorId, string Email, string CertificateThumbprint);

public enum OperatorAuthError
{
    ChallengeNotFound,
    ChallengeExpired,
    InvalidSignature,
    CertificateNotTrusted,
    CertificateRevoked,
    OperatorNotFound,
    OperatorInactive
}

[Alias("Argon.Grains.Interfaces.IOperatorAuthChallengeGrain")]
public interface IOperatorAuthChallengeGrain : IGrainWithStringKey
{
    [Alias(nameof(CreateChallenge))]
    Task<OperatorChallengeData> CreateChallenge();

    [Alias(nameof(VerifyChallenge))]
    Task<Either<OperatorAuthSuccess, OperatorAuthError>> VerifyChallenge(
        string challengeId, byte[] signature, byte[] certificateDer);
}
