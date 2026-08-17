namespace Argon.Features.AccountConsole;

using Argon.Features.Clustering;

/// <summary>
/// Where the developer console's tokens come from. Same OAuth provider the operator console uses,
/// but the console's own audience — an operator token must not open the developer surface, and a
/// developer token must not open the operator one.
/// </summary>
public record AccountConsoleAuthOptions : IValidatableFeatureOptions
{
    public const string SectionName = "AccountConsoleAuth";

    public string MetadataAddress { get; set; } = "";
    public string ValidIssuer     { get; set; } = "";

    public List<string> ValidAudiences { get; set; } = [];

    public void Validate(IFeatureConfigurationReport report)
    {
        if (!report.SectionExists)
            return;

        report.RequireUri(MetadataAddress, nameof(MetadataAddress), "https", "http");
        report.RequireUri(ValidIssuer, nameof(ValidIssuer), "https", "http");

        // An empty audience list turns audience validation off, which would let a token minted for
        // any other client of the same issuer in through the developer door.
        report.Require(ValidAudiences.Count > 0, nameof(ValidAudiences),
            "is empty, so any token this issuer signed would be accepted");
    }
}
