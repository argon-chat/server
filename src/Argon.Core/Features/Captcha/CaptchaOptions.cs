namespace Argon.Features.Captcha;

public class CaptchaOptions
{
    public string      SiteKey           { get; set; }
    public string      SiteSecret        { get; set; }
    public string      ChallengeEndpoint { get; set; }
    public CaptchaKind Kind              { get; set; }
}