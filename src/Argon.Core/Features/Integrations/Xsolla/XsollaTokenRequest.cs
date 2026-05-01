namespace Argon.Core.Features.Integrations.Xsolla;

using System.Text.Json.Serialization;

/// <summary>
/// Xsolla Pay Station token creation request.
/// POST /merchants/{merchant_id}/token
/// https://developers.xsolla.com/api/pay-station/token/create-token
/// </summary>
public sealed class XsollaTokenRequest
{
    [JsonPropertyName("user")]
    public required XsollaTokenUser User { get; init; }

    [JsonPropertyName("settings")]
    public required XsollaTokenSettings Settings { get; init; }

    [JsonPropertyName("purchase")]
    public XsollaTokenPurchase? Purchase { get; init; }

    [JsonPropertyName("custom_parameters")]
    public Dictionary<string, object>? CustomParameters { get; init; }
}

public sealed class XsollaTokenUser
{
    [JsonPropertyName("id")]
    public required XsollaStringValue Id { get; init; }

    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaStringValue? Email { get; init; }

    [JsonPropertyName("country")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCountryValue? Country { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaModifiableValue? Name { get; init; }
}

public sealed class XsollaStringValue
{
    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

public sealed class XsollaModifiableValue
{
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("allow_modify")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowModify { get; init; }
}

public sealed class XsollaCountryValue
{
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("allow_modify")]
    public bool AllowModify { get; init; }
}

public sealed class XsollaTokenSettings
{
    [JsonPropertyName("project_id")]
    public required int ProjectId { get; init; }

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }

    [JsonPropertyName("return_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnUrl { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }

    [JsonPropertyName("external_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; init; }

    [JsonPropertyName("redirect_policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaRedirectPolicy? RedirectPolicy { get; init; }

    [JsonPropertyName("ui")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaUiSettings? Ui { get; init; }
}

public sealed class XsollaRedirectPolicy
{
    [JsonPropertyName("redirect_conditions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RedirectConditions { get; init; }

    [JsonPropertyName("delay")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Delay { get; init; }

    [JsonPropertyName("status_for_manual_redirection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusForManualRedirection { get; init; }

    [JsonPropertyName("manual_redirection_action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManualRedirectionAction { get; init; }
}

public sealed class XsollaUiSettings
{
    [JsonPropertyName("theme")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Theme { get; init; }

    [JsonPropertyName("is_three_ds_independent_windows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsThreeDsIndependentWindows { get; init; }

    [JsonPropertyName("desktop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaDesktopSettings? Desktop { get; init; }
}

public sealed class XsollaDesktopSettings
{
    [JsonPropertyName("header")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaHeaderSettings? Header { get; init; }
}

public sealed class XsollaHeaderSettings
{
    [JsonPropertyName("is_visible")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsVisible { get; init; }

    [JsonPropertyName("close_button")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CloseButton { get; init; }
}

public sealed class XsollaTokenPurchase
{
    [JsonPropertyName("subscription")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaSubscriptionPurchase? Subscription { get; init; }

    [JsonPropertyName("virtual_items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaVirtualItemsPurchase? VirtualItems { get; init; }
}

public sealed class XsollaSubscriptionPurchase
{
    [JsonPropertyName("plan_id")]
    public required string PlanId { get; init; }

    [JsonPropertyName("force")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Force { get; init; }
}

public sealed class XsollaVirtualItemsPurchase
{
    [JsonPropertyName("items")]
    public required XsollaVirtualItem[] Items { get; init; }
}

public sealed class XsollaVirtualItem
{
    [JsonPropertyName("sku")]
    public required string Sku { get; init; }

    [JsonPropertyName("amount")]
    public required int Amount { get; init; }
}
