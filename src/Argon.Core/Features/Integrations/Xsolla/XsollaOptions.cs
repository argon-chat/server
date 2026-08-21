namespace Argon.Core.Features.Integrations.Xsolla;

public class XsollaOptions
{
    public int    ProjectId       { get; set; }
    public int    MerchantId      { get; set; }
    public string ApiKey          { get; set; } = string.Empty;
    public string WebhookSecret   { get; set; } = string.Empty;
    public bool   IsSandbox       { get; set; } = true;
    public string LoginProjectId  { get; set; } = string.Empty;

    /// <summary>Default payment method ID shown in Pay Station (e.g. 1380 = Card).</summary>
    public int    PaymentMethodId         { get; set; } = 1380;

    /// <summary>Server OAuth 2.0 client ID for Xsolla Login (server-to-server).</summary>
    public int    ServerOAuthClientId     { get; set; }
    /// <summary>Server OAuth 2.0 client secret for Xsolla Login.</summary>
    public string ServerOAuthClientSecret { get; set; } = string.Empty;
}
