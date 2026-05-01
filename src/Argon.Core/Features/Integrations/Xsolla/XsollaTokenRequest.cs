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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTokenPurchase? Purchase { get; init; }

    [JsonPropertyName("custom_parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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

    [JsonPropertyName("age")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Age { get; init; }

    [JsonPropertyName("phone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPhoneValue? Phone { get; init; }

    [JsonPropertyName("public_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaStringValue? PublicId { get; init; }

    [JsonPropertyName("steam_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaStringValue? SteamId { get; init; }

    [JsonPropertyName("tracking_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaStringValue? TrackingId { get; init; }

    [JsonPropertyName("utm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaUtmData? Utm { get; init; }

    [JsonPropertyName("is_legal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsLegal { get; init; }

    [JsonPropertyName("legal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaLegalEntity? Legal { get; init; }

    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Attributes { get; init; }
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

public sealed class XsollaPhoneValue
{
    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

public sealed class XsollaUtmData
{
    [JsonPropertyName("utm_source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UtmSource { get; init; }

    [JsonPropertyName("utm_medium")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UtmMedium { get; init; }

    [JsonPropertyName("utm_campaign")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UtmCampaign { get; init; }

    [JsonPropertyName("utm_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UtmContent { get; init; }

    [JsonPropertyName("utm_term")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UtmTerm { get; init; }
}

public sealed class XsollaLegalEntity
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; init; }

    [JsonPropertyName("country")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Country { get; init; }

    [JsonPropertyName("vat_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VatId { get; init; }
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

    [JsonPropertyName("cancel_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CancelUrl { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }

    [JsonPropertyName("external_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; init; }

    [JsonPropertyName("payment_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PaymentMethod { get; init; }

    [JsonPropertyName("payment_widget")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PaymentWidget { get; init; }

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

    [JsonPropertyName("redirect_button_caption")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RedirectButtonCaption { get; init; }

    [JsonPropertyName("show_redirect_countdown")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowRedirectCountdown { get; init; }
}

public sealed class XsollaUiSettings
{
    [JsonPropertyName("theme")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Theme { get; init; }

    [JsonPropertyName("is_three_ds_independent_windows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsThreeDsIndependentWindows { get; init; }

    [JsonPropertyName("is_payment_methods_list_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsPaymentMethodsListMode { get; init; }

    [JsonPropertyName("is_search_field_hidden")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsSearchFieldHidden { get; init; }

    [JsonPropertyName("is_cart_open_by_default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsCartOpenByDefault { get; init; }

    [JsonPropertyName("is_independent_windows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsIndependentWindows { get; init; }

    [JsonPropertyName("is_language_selector_hidden")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsLanguageSelectorHidden { get; init; }

    [JsonPropertyName("is_prevent_external_link_open")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsPreventExternalLinkOpen { get; init; }

    [JsonPropertyName("is_show_close_widget_warning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsShowCloseWidgetWarning { get; init; }

    [JsonPropertyName("alternative_first_screen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AlternativeFirstScreen { get; init; }

    [JsonPropertyName("apple_pay_quick_payment_button")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ApplePayQuickPaymentButton { get; init; }

    [JsonPropertyName("gp_quick_payment_button")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? GpQuickPaymentButton { get; init; }

    [JsonPropertyName("currency_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrencyFormat { get; init; }

    [JsonPropertyName("layout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Layout { get; init; }

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }

    [JsonPropertyName("desktop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaDesktopSettings? Desktop { get; init; }

    [JsonPropertyName("mobile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaMobileSettings? Mobile { get; init; }

    [JsonPropertyName("header")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaUiHeaderSettings? Header { get; init; }

    [JsonPropertyName("components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaUiComponents? Components { get; init; }

    [JsonPropertyName("user_account")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaUserAccountSettings? UserAccount { get; init; }
}

public sealed class XsollaUiHeaderSettings
{
    [JsonPropertyName("visible_virtual_currency_balance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? VisibleVirtualCurrencyBalance { get; init; }
}

public sealed class XsollaUiComponents
{
    [JsonPropertyName("virtual_items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaVirtualItemsComponent? VirtualItems { get; init; }

    [JsonPropertyName("virtual_currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaVirtualCurrencyComponent? VirtualCurrency { get; init; }

    [JsonPropertyName("subscriptions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaSubscriptionsComponent? Subscriptions { get; init; }
}

public sealed class XsollaVirtualItemsComponent
{
    [JsonPropertyName("hidden")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Hidden { get; init; }

    [JsonPropertyName("order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Order { get; init; }

    [JsonPropertyName("selected_group")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedGroup { get; init; }

    [JsonPropertyName("selected_item")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedItem { get; init; }
}

public sealed class XsollaVirtualCurrencyComponent
{
    [JsonPropertyName("hidden")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Hidden { get; init; }

    [JsonPropertyName("order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Order { get; init; }

    [JsonPropertyName("custom_amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CustomAmount { get; init; }
}

public sealed class XsollaSubscriptionsComponent
{
    [JsonPropertyName("hidden")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Hidden { get; init; }

    [JsonPropertyName("order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Order { get; init; }
}

public sealed class XsollaUserAccountSettings
{
    [JsonPropertyName("payment_accounts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPaymentAccountsSettings? PaymentAccounts { get; init; }
}

public sealed class XsollaPaymentAccountsSettings
{
    [JsonPropertyName("enable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enable { get; init; }

    [JsonPropertyName("order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Order { get; init; }
}

public sealed class XsollaMobileSettings
{
    [JsonPropertyName("header")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaMobileHeaderSettings? Header { get; init; }
}

public sealed class XsollaMobileHeaderSettings
{
    [JsonPropertyName("close_button")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CloseButton { get; init; }

    [JsonPropertyName("close_button_icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CloseButtonIcon { get; init; }
}

public sealed class XsollaDesktopSettings
{
    [JsonPropertyName("header")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaHeaderSettings? Header { get; init; }

    [JsonPropertyName("subscription_list")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaSubscriptionListSettings? SubscriptionList { get; init; }
}

public sealed class XsollaSubscriptionListSettings
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("display_local_price")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DisplayLocalPrice { get; init; }
}

public sealed class XsollaHeaderSettings
{
    [JsonPropertyName("is_visible")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsVisible { get; init; }

    [JsonPropertyName("close_button")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CloseButton { get; init; }

    [JsonPropertyName("close_button_icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CloseButtonIcon { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("visible_logo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? VisibleLogo { get; init; }

    [JsonPropertyName("visible_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? VisibleName { get; init; }

    [JsonPropertyName("visible_purchase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? VisiblePurchase { get; init; }
}

public sealed class XsollaTokenPurchase
{
    [JsonPropertyName("subscription")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaSubscriptionPurchase? Subscription { get; init; }

    [JsonPropertyName("is_lootbox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsLootbox { get; init; }

    [JsonPropertyName("checkout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCheckout? Checkout { get; init; }
}

public sealed class XsollaCheckout
{
    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }
}

public sealed class XsollaSubscriptionPurchase
{
    [JsonPropertyName("plan_id")]
    public required string PlanId { get; init; }

    [JsonPropertyName("product_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProductId { get; init; }

    [JsonPropertyName("operation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("trial_days")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TrialDays { get; init; }

    [JsonPropertyName("available_plans")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? AvailablePlans { get; init; }
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
