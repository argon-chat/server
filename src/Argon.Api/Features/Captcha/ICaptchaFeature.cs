namespace Argon.Features.Captcha;

public interface ICaptchaFeature
{
    ValueTask<bool> ValidateAsync(string token);
}