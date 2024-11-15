namespace Argon.Api.Features.Captcha;

public class YandexCaptcha : ICaptchaFeature
{
    public ValueTask<bool> ValidateAsync(string token) => throw new NotImplementedException();
}