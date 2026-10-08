namespace Argon.Core.Features.Integrations.Captcha;

public interface ICaptchaFeature
{
    ValueTask<bool> ValidateAsync(string token);
}