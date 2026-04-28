namespace Argon.Core.Features.Integrations.Xsolla;

public class XsollaOptions
{
    public int    ProjectId       { get; set; }
    public int    MerchantId      { get; set; }
    public string ApiKey          { get; set; } = string.Empty;
    public string WebhookSecret   { get; set; } = string.Empty;
    public bool   IsSandbox       { get; set; } = true;
    public int    LoginProjectId  { get; set; }
}
