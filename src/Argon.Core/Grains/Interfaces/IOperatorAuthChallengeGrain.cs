namespace Argon.Grains.Interfaces;

public record OperatorChallengeData(string ChallengeId, byte[] ChallengeBytes);

public record OperatorAuthSuccess(
    Guid OperatorId,
    string Email,
    string CertificateThumbprint,
    string DisplayName,
    bool IsSystemOperator);

public enum OperatorAuthError
{
    ChallengeNotFound,
    ChallengeExpired,
    InvalidSignature,
    CertificateNotTrusted,
    CertificateRevoked,
    OperatorNotFound,
    OperatorInactive,

    /// <summary>
    /// The certificate is a valid operator certificate, but not this signed-in user's. Only the
    /// mutual-TLS path can produce it: the challenge path has no session to compare against.
    /// </summary>
    CertificateUserMismatch
}

[Alias("Argon.Grains.Interfaces.IOperatorAuthChallengeGrain")]
public interface IOperatorAuthChallengeGrain : IGrainWithStringKey
{
    [Alias(nameof(CreateChallenge))]
    Task<OperatorChallengeData> CreateChallenge();

    [Alias(nameof(VerifyChallenge))]
    Task<Either<OperatorAuthSuccess, OperatorAuthError>> VerifyChallenge(
        string challengeId, byte[] signature, byte[] certificateDer);

    /// <summary>
    /// Verifies a certificate that was presented in a TLS handshake rather than signed over a
    /// challenge, and checks it belongs to <paramref name="userId"/>.
    /// </summary>
    /// <remarks>
    /// No challenge and no signature, because the handshake already proved possession of the private
    /// key — repeating that here would only re-prove it. What this still owes is everything the
    /// handshake does not say: that the chain leads to the operator CA, that some operator holds
    /// this certificate, that they are active, and that they are the person whose session is asking.
    /// <para>
    /// Key this grain by the certificate's thumbprint, which is the hex SHA-256 of
    /// <paramref name="certificateDer"/> itself.
    /// </para>
    /// </remarks>
    [Alias(nameof(VerifyMutualTlsCertificate))]
    Task<Either<OperatorAuthSuccess, OperatorAuthError>> VerifyMutualTlsCertificate(
        byte[] certificateDer, Guid userId);
}
