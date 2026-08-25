namespace Argon.Api.Features.Aegis;

using Argon.Features.Aegis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

/// <summary>
/// Sending mail through Argon's SMTP on an application's behalf.
/// </summary>
/// <remarks>
/// Here rather than on the entry point because the credential is an OAuth token this server issued,
/// and this is where tokens are understood. The scope is checked separately from the token being
/// valid: a client-credentials token is minted for whatever scopes the application is registered
/// for, and only one of them may send mail.
/// </remarks>
[ApiController, Route("api/email")]
public class EmailSendController(
    IClusterClient cluster,
    ILogger<EmailSendController> logger) : ControllerBase
{
    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] EmailSendRequest request, CancellationToken ct)
    {
        var authentication = await HttpContext.AuthenticateAsync(
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        if (!authentication.Succeeded || authentication.Principal is null)
            return Unauthorized(new { error = "unauthorized", message = "Invalid or expired access token." });

        var principal = authentication.Principal;

        if (!principal.GetScopes().Contains(ArgonScopes.EmailSend))
            return StatusCode(403, new
            {
                error   = "scope_insufficient",
                message = $"Token does not have the '{ArgonScopes.EmailSend}' scope."
            });

        var clientId = principal.GetClaim(OpenIddictConstants.Claims.ClientId) ?? "unknown";

        if (string.IsNullOrWhiteSpace(request.To))
            return BadRequest(new { error = "invalid_request", message = "The 'to' field is required." });
        if (string.IsNullOrWhiteSpace(request.Subject))
            return BadRequest(new { error = "invalid_request", message = "The 'subject' field is required." });
        if (string.IsNullOrWhiteSpace(request.Html))
            return BadRequest(new { error = "invalid_request", message = "The 'html' field is required." });

        var email = cluster.GetGrain<IEmailManager>(Guid.Empty);

        // Checked before sending rather than after failing: an address that will bounce costs the
        // sending domain's reputation, which is shared with everything else Argon mails.
        var destination = await email.ValidateEMailDestination(request.To, ct);

        if (!destination.CanSendEmail)
        {
            logger.LogWarning("[EmailSend] Invalid recipient {To}: {Reason}", request.To, destination.FailureReason);

            return UnprocessableEntity(new
            {
                error   = "invalid_recipient",
                message = $"Invalid email: {destination.FailureReason}"
            });
        }

        try
        {
            var messageId = await email.SendRawAsync(
                request.To, request.Subject, request.Html, request.From, request.ReplyTo);

            logger.LogInformation("[EmailSend] clientId={ClientId}, to={To}, subject={Subject}, messageId={MessageId}",
                clientId, request.To, request.Subject, messageId);

            return Ok(new { success = true, message_id = messageId });
        }
        catch (Exception e)
        {
            logger.LogError(e, "[EmailSend] SMTP error for clientId={ClientId}, to={To}", clientId, request.To);
            return StatusCode(500, new { error = "send_failed", message = "Failed to send email via SMTP." });
        }
    }
}

public record EmailSendRequest
{
    public string  To      { get; init; } = "";
    public string  Subject { get; init; } = "";
    public string  Html    { get; init; } = "";
    public string? From    { get; init; }
    public string? ReplyTo { get; init; }
}
