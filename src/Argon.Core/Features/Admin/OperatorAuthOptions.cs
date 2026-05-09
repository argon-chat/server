namespace Argon.Features.Admin;

public record OperatorAuthOptions
{
    public const string SectionName = "OperatorAuth";

    public string MetadataAddress { get; set; } = "";
    public string ValidIssuer     { get; set; } = "";
}
