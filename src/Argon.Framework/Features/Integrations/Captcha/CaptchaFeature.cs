namespace Argon.Core.Features.Integrations.Captcha;

using Microsoft.Extensions.Configuration;

public static class CaptchaFeature
{
    /// <summary>
    /// Which provider answers depends on configuration, so the kind is passed in rather than read
    /// here: the feature that owns the <c>Captcha</c> section is where it is bound and validated.
    /// </summary>
    public static IServiceCollection AddCaptchaFeature(this WebApplicationBuilder builder, CaptchaKind kind)
    {
        return kind switch
        {
            CaptchaKind.NO_CAPTCHA => builder.Services.AddTransient<ICaptchaFeature, NullCaptcha>(),
            CaptchaKind.CLOUDFLARE => builder.Services.AddTransient<ICaptchaFeature, CloudflareCaptcha>(),
            CaptchaKind.YANDEX     => builder.Services.AddTransient<ICaptchaFeature, YandexCaptcha>(),
            _                      => throw new ArgumentOutOfRangeException()
        };
    }
}