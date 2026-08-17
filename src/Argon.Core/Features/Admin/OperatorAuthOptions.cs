namespace Argon.Features.Admin;

using Argon.Features.Clustering;

/// <summary>
/// Where the admin console's operator tokens come from.
/// </summary>
public record OperatorAuthOptions : IValidatableFeatureOptions
{
    public const string SectionName = "OperatorAuth";

    public string MetadataAddress { get; set; } = "";
    public string ValidIssuer     { get; set; } = "";

    /// <remarks>
    /// Left non-required so a role that does not host the admin console can leave the section out
    /// entirely. The interceptor refuses every call when it finds these empty, which is the safe
    /// direction; validation is here to catch the half-configured case before it gets that far.
    /// </remarks>
    public void Validate(IFeatureConfigurationReport report)
    {
        if (!report.SectionExists)
            return;

        report.RequireUri(MetadataAddress, nameof(MetadataAddress), "https", "http");
        report.RequireUri(ValidIssuer, nameof(ValidIssuer), "https", "http");
    }
}
