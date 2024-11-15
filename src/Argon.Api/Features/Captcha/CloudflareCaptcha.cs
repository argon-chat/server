namespace Argon.Api.Features.Captcha;

using Extensions;
using Flurl.Http;
using Microsoft.Extensions.Options;

public class CloudflareCaptcha(HttpContext httpContext, ILogger<ICaptchaFeature> logger, IOptions<CaptchaOptions> options) : ICaptchaFeature
{
    public async ValueTask<bool> ValidateAsync(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        var config   = options.Value;
        var remoteIp = httpContext.GetIpAddress();
        try
        {
            var response = await config.ChallengeEndpoint
               .PostMultipartAsync(content => content
                   .AddString("secret", config.SiteSecret)
                   .AddString("response", token)
                   .AddString("remoteip", remoteIp))
               .ReceiveJson<CloudflareTurnstileResponse>();
            logger.LogInformation("Success validate captcha token {Challenge_ts} {Hostname}", response.Challenge_ts, response.Hostname);
            return response?.Success ?? false;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "failed validate captcha token");
            return false;
        }
    }

    public class CloudflareTurnstileResponse
    {
        public bool     Success      { get; set; }
        public string   Challenge_ts { get; set; }
        public string   Hostname     { get; set; }
        public string[] ErrorCodes   { get; set; }
    }

}