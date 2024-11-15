namespace Argon.Api.Features.Captcha;

public class NullCaptcha : ICaptchaFeature
{
    public ValueTask<bool> ValidateAsync(string token) => new(true);
}