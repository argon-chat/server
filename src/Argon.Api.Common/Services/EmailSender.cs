using Argon.Api.Common.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Argon.Api.Common.Services;

public class EmailSender(ILogger<EmailSender> logger) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        logger.LogInformation(
            "Sending confirmation link to {email} for user {user} with confirmation link {confirmationLink}", email,
            user.UserName, confirmationLink);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
    {
        logger.LogInformation("Sending password reset link to {email} for user {user} with reset link {resetLink}",
            email, user.UserName, resetLink);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
    {
        logger.LogInformation("Sending password reset code to {email} for user {user} with reset code {resetCode}",
            email, user.UserName, resetCode);
        return Task.CompletedTask;
    }
}