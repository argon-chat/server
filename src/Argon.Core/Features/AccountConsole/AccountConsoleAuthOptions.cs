namespace Argon.Features.AccountConsole;

/// <summary>
/// Where the developer console's tokens come from. Same OAuth provider the operator console uses,
/// but the console's own audience — an operator token must not open the developer surface, and a
/// developer token must not open the operator one.
/// </summary>
public record AccountConsoleAuthOptions
{
    public const string SectionName = "AccountConsoleAuth";

    public string MetadataAddress { get; set; } = "";
    public string ValidIssuer     { get; set; } = "";

    public List<string> ValidAudiences { get; set; } = [];
}
