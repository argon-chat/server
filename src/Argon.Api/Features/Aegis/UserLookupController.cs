namespace Argon.Api.Features.Aegis;

using Argon.Features.Aegis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>
/// Resolving an Argon account for an application that already knows who it wants — the magic-link
/// case, where a mail arrives addressed to somebody and the application has to find them.
/// </summary>
/// <remarks>
/// Not OAuth, and not part of any user's session: the caller is an application authenticating with
/// its own client credentials, and the subject never sees this happen. That makes the entitlement
/// the whole story — the credentials must be right, and the application must carry the magic-link
/// permission, which is granted per application rather than assumed. Without the second check any
/// registered client could turn this into an address-book.
/// </remarks>
[ApiController, Route("api/users")]
[EnableRateLimiting(AegisRateLimitOptions.AuthPolicy)]
public class UserLookupController(
    IAegisDirectory directory,
    ILogger<UserLookupController> logger) : ControllerBase
{
    [HttpGet("by-email/{email}")]
    public async Task<IActionResult> GetByEmail(
        [FromHeader(Name = "X-Client-Id")] string clientId,
        [FromHeader(Name = "X-Client-Secret")] string clientSecret,
        string email,
        CancellationToken ct)
    {
        if (await AuthorizeClientAsync(clientId, clientSecret, ct) is { } refusal)
            return refusal;

        var userId = await directory.GetUserIdByEmailAsync(email, ct);

        return userId is null
            ? NotFound(new { error = "user_not_found" })
            : await DescribeAsync(userId.Value, ct);
    }

    [HttpGet("by-id/{userId:guid}")]
    public async Task<IActionResult> GetById(
        [FromHeader(Name = "X-Client-Id")] string clientId,
        [FromHeader(Name = "X-Client-Secret")] string clientSecret,
        Guid userId,
        CancellationToken ct)
    {
        if (await AuthorizeClientAsync(clientId, clientSecret, ct) is { } refusal)
            return refusal;

        return await DescribeAsync(userId, ct);
    }

    /// <summary>The refusal to send back, or <c>null</c> when the caller may proceed.</summary>
    private async Task<IActionResult?> AuthorizeClientAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return Unauthorized(new
            {
                error             = "invalid_client",
                error_description = "X-Client-Id and X-Client-Secret headers are required."
            });

        var credentials = await directory.GetAppCredentialsAsync(clientId, ct);

        if (credentials is null || !ClientSecret.Matches(credentials.ClientSecret, clientSecret))
        {
            logger.LogWarning("[UserLookup] Invalid client credentials for clientId={ClientId}", clientId);
            return Unauthorized(new { error = "invalid_client" });
        }

        if (!credentials.AllowMagicLink)
        {
            logger.LogWarning("[UserLookup] Lookup not allowed for clientId={ClientId} (AllowMagicLink=false)", clientId);

            return StatusCode(403, new
            {
                error             = "forbidden",
                error_description = "This application does not have user lookup permissions."
            });
        }

        return null;
    }

    private async Task<IActionResult> DescribeAsync(Guid userId, CancellationToken ct)
    {
        var user = await directory.GetUserAsync(userId, ct);

        if (user is null)
            return NotFound(new { error = "user_not_found" });

        return Ok(new
        {
            userId       = user.UserId,
            username     = user.Username,
            email        = await directory.GetUserEmailAsync(userId, ct),
            avatarFileId = user.AvatarFileId
        });
    }
}
