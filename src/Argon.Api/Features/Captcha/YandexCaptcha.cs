namespace Argon.Features.Captcha;

public class YandexCaptcha : ICaptchaFeature
{
    public ValueTask<bool> ValidateAsync(string token) => throw new NotImplementedException();
}