namespace Argon.Api.Features.Aegis;

using Argon.Features.Aegis;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Staff step-up: proving with a hardware key that the signed-in user is the operator they claim
/// to be.
/// </summary>
/// <remarks>
/// Reached over a mutual-TLS route, so the certificate has already been presented by the time this
/// runs and there is nothing to challenge — the handshake did the proving. What is left is checking
/// it belongs to an active operator and to <i>this</i> session, which is the grain's job, and
/// remembering the result server-side until the authorization it unlocks completes.
/// <para>
/// The certificate reaches this process in a header, because TLS terminates at the proxy. That only
/// works if the proxy overwrites the header from the real handshake on this route and strips it
/// everywhere else — otherwise anyone may send one.
/// </para>
/// </remarks>
[ApiController, Route("api/auth/operator")]
public class OperatorAuthController(
    IClusterClient cluster,
    IOperatorVerificationStore verifications,
    AegisSession session,
    ILogger<OperatorAuthController> logger) : ControllerBase
{
    [HttpPost("verify")]
    public async Task<IActionResult> VerifyCertificate()
    {
        if (!session.IsAuthenticated)
            return Unauthorized(new { error = "not_authenticated" });

        var userId      = session.RequireUserId;
        var certificate = HttpContext.Connection.ClientCertificate;

        if (certificate is null)
        {
            logger.LogWarning("[Operator] No client certificate presented for user {UserId}", userId);

            return BadRequest(new { error = "no_certificate" });
        }

        var raw = certificate.RawData;

        // The grain is keyed by the thumbprint so repeat step-ups from one device land on one
        // activation; hex SHA-256 of the DER is exactly what the certificate's own hash is.
        var thumbprint = Convert.ToHexString(SHA256.HashData(raw));

        var result = await cluster
           .GetGrain<IOperatorAuthChallengeGrain>(thumbprint)
           .VerifyMutualTlsCertificate(raw, userId);

        if (!result.IsSuccess)
        {
            logger.LogWarning("[Operator] Certificate rejected for user {UserId}: {Error}", userId, result.Error);

            return BadRequest(new
            {
                error  = "certificate_validation_failed",
                reason = ReasonOf(result.Error)
            });
        }

        var op = result.Value;

        await verifications.StoreAsync(userId,
            new OperatorVerificationState(op.OperatorId, op.Email, op.CertificateThumbprint, op.DisplayName, op.IsSystemOperator),
            HttpContext.RequestAborted);

        logger.LogInformation("[Operator] Operator {OperatorId} verified via mTLS for user {UserId}", op.OperatorId, userId);

        return Ok(new
        {
            success       = true,
            operatorId    = op.OperatorId,
            operatorEmail = op.Email
        });
    }

    /// <summary>
    /// What to tell the person at the keyboard, as a code the widget translates.
    /// </summary>
    /// <remarks>
    /// Deliberately vaguer than the log line beside it. Which of these it was is useful to whoever
    /// is debugging and useful to whoever is probing, so the detail goes to the log and the person
    /// gets the part they can act on.
    /// </remarks>
    private static string ReasonOf(OperatorAuthError error)
        => error switch
        {
            OperatorAuthError.CertificateNotTrusted   => "certificate_not_trusted",
            OperatorAuthError.CertificateRevoked      => "certificate_revoked",
            OperatorAuthError.CertificateUserMismatch => "certificate_user_mismatch",
            OperatorAuthError.OperatorInactive        => "operator_inactive",
            OperatorAuthError.OperatorNotFound        => "operator_not_found",
            _                                         => "certificate_unverified"
        };
}
