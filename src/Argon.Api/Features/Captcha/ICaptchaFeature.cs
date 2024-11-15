namespace Argon.Api.Features.Captcha;

public interface ICaptchaFeature
{
    ValueTask<bool> ValidateAsync(string token);
}