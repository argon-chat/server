namespace Argon.Core.Features.Integrations.Captcha;

using Argon.Features.Clustering;

public class CaptchaOptions : IValidatableFeatureOptions
{
    public string      SiteKey           { get; set; }
    public string      SiteSecret        { get; set; }
    public string      ChallengeEndpoint { get; set; }
    public CaptchaKind Kind              { get; set; }

    /// <summary>
    /// Conditional, so it cannot be said with attributes: the keys matter only once a provider is
    /// chosen, and <see cref="CaptchaKind.NO_CAPTCHA"/> is a legitimate choice.
    /// </summary>
    public void Validate(IFeatureConfigurationReport report)
    {
        if (Kind is CaptchaKind.NO_CAPTCHA)
            return;

        report.Required(SiteKey, nameof(SiteKey));
        report.Required(SiteSecret, nameof(SiteSecret));
    }
}
