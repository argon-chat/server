namespace Argon.Core.Features.Integrations.Captcha;

using Microsoft.Extensions.Configuration;

public static class CaptchaFeature
{
    public static IServiceCollection AddCaptchaFeature(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration.GetSection("Captcha");
        builder.Services.Configure<CaptchaOptions>(cfg);
        var kind = cfg.GetValue<CaptchaKind>("Kind");

        return kind switch
        {
            CaptchaKind.NO_CAPTCHA => builder.Services.AddTransient<ICaptchaFeature, NullCaptcha>(),
            CaptchaKind.CLOUDFLARE => builder.Services.AddTransient<ICaptchaFeature, CloudflareCaptcha>(),
            CaptchaKind.YANDEX     => builder.Services.AddTransient<ICaptchaFeature, YandexCaptcha>(),
            _                      => throw new ArgumentOutOfRangeException()
        };
    }
}