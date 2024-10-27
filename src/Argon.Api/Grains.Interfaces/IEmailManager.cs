namespace Argon.Api.Grains.Interfaces;

[Alias("Argon.Api.Grains.Interfaces.IEmailManager")]
public interface IEmailManager : IGrainWithGuidKey
{
    [Alias("SendEmailAsync")]
    Task SendEmailAsync(string email, string subject, string message, string layout = "base");
}