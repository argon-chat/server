namespace Argon.Core.Features.Integrations.Captcha;

public class YandexCaptcha : ICaptchaFeature
{
    public ValueTask<bool> ValidateAsync(string token) => throw new NotImplementedException();
}