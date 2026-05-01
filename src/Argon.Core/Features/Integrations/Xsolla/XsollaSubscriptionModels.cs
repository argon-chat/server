namespace Argon.Core.Features.Integrations.Xsolla;

using System.Text.Json.Serialization;

// ═══════════════════════════════════════════════════════════════════════════
// Shared Nested Types (used across Subscriptions API)
// ═══════════════════════════════════════════════════════════════════════════

public sealed class XsollaCharge
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }

    [JsonPropertyName("amount_with_promotion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? AmountWithPromotion { get; init; }

    [JsonPropertyName("setup_fee")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? SetupFee { get; init; }
}

public sealed class XsollaPeriod
{
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Value { get; init; }

    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }
}

public sealed class XsollaChargePeriod
{
    [JsonPropertyName("value")]
    public int Value { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public sealed class XsollaPromotion
{
    [JsonPropertyName("promotion_charge_amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? PromotionChargeAmount { get; init; }

    [JsonPropertyName("promotion_remaining_charges")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PromotionRemainingCharges { get; init; }
}

public sealed class XsollaPaymentRecurrentAccount
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("ps_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PsName { get; init; }

    [JsonPropertyName("switch_icon_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SwitchIconName { get; init; }

    [JsonPropertyName("card_expiry_date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCardExpiryDate? CardExpiryDate { get; init; }
}

public sealed class XsollaCardExpiryDate
{
    [JsonPropertyName("month")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Month { get; init; }

    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Year { get; init; }
}

public sealed class XsollaLastSuccessfulCharge
{
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }
}

public sealed class XsollaSurcharge
{
    [JsonPropertyName("surcharge_amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? SurchargeAmount { get; init; }

    [JsonPropertyName("surcharge_currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SurchargeCurrency { get; init; }
}

public sealed class XsollaUnused
{
    [JsonPropertyName("unused_amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? UnusedAmount { get; init; }

    [JsonPropertyName("unused_currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnusedCurrency { get; init; }
}

public sealed class XsollaSubscriptionPaymentDetails
{
    [JsonPropertyName("surcharge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaSurcharge? Surcharge { get; init; }

    [JsonPropertyName("unused")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaUnused? Unused { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Localized Strings
// ═══════════════════════════════════════════════════════════════════════════

public sealed class XsollaLocalizedStrings
{
    [JsonPropertyName("en")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? En { get; init; }

    [JsonPropertyName("ru")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ru { get; init; }

    [JsonPropertyName("de")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? De { get; init; }

    [JsonPropertyName("fr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fr { get; init; }

    [JsonPropertyName("es")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Es { get; init; }

    [JsonPropertyName("pt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pt { get; init; }

    [JsonPropertyName("it")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? It { get; init; }

    [JsonPropertyName("ja")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ja { get; init; }

    [JsonPropertyName("ko")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ko { get; init; }

    [JsonPropertyName("cn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cn { get; init; }

    [JsonPropertyName("tw")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tw { get; init; }

    [JsonPropertyName("ar")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ar { get; init; }

    [JsonPropertyName("bg")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bg { get; init; }

    [JsonPropertyName("cs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cs { get; init; }

    [JsonPropertyName("he")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? He { get; init; }

    [JsonPropertyName("pl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pl { get; init; }

    [JsonPropertyName("ro")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ro { get; init; }

    [JsonPropertyName("th")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Th { get; init; }

    [JsonPropertyName("tr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tr { get; init; }

    [JsonPropertyName("vi")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Vi { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Plans — List / CRUD
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response from GET /projects/{project_id}/subscriptions/plans
/// </summary>
public sealed class XsollaSubscriptionPlanListResponse
{
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaSubscriptionPlanResponse[]? Items { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }
}

/// <summary>
/// Full plan response (from GET plans list or GET plan by id).
/// Matches SubscriptionsPlan schema.
/// </summary>
public sealed class XsollaSubscriptionPlanResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("external_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; init; }

    [JsonPropertyName("project_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProjectId { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaLocalizedStrings? Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaLocalizedStrings? Description { get; init; }

    [JsonPropertyName("group_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanStatus? Status { get; init; }

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Tags { get; init; }

    [JsonPropertyName("charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanCharge? Charge { get; init; }

    [JsonPropertyName("expiration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanExpiration? Expiration { get; init; }

    [JsonPropertyName("trial")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanTrial? Trial { get; init; }

    [JsonPropertyName("grace_period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanGracePeriod? GracePeriod { get; init; }

    [JsonPropertyName("billing_retry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanBillingRetry? BillingRetry { get; init; }

    [JsonPropertyName("refund_period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RefundPeriod { get; init; }
}

public sealed class XsollaPlanStatus
{
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; init; }
}

public sealed class XsollaPlanCharge
{
    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaChargePeriod? Period { get; init; }

    [JsonPropertyName("prices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanPrice[]? Prices { get; init; }
}

public sealed class XsollaPlanPrice
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }

    [JsonPropertyName("setup_fee")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? SetupFee { get; init; }
}

public sealed class XsollaPlanExpiration
{
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Value { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public sealed class XsollaPlanTrial
{
    [JsonPropertyName("value")]
    public int Value { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public sealed class XsollaPlanGracePeriod
{
    [JsonPropertyName("value")]
    public int Value { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public sealed class XsollaPlanBillingRetry
{
    [JsonPropertyName("value")]
    public int Value { get; init; }
}

/// <summary>
/// Request for POST /projects/{project_id}/subscriptions/plans (create) and
/// PUT /projects/{project_id}/subscriptions/plans/{plan_id} (update)
/// </summary>
public sealed class XsollaSubscriptionPlanRequest
{
    [JsonPropertyName("external_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaLocalizedStrings? Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaLocalizedStrings? Description { get; init; }

    [JsonPropertyName("group_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; init; }

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Tags { get; init; }

    [JsonPropertyName("charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanCharge? Charge { get; init; }

    [JsonPropertyName("expiration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanExpiration? Expiration { get; init; }

    [JsonPropertyName("trial")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanTrial? Trial { get; init; }

    [JsonPropertyName("grace_period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanGracePeriod? GracePeriod { get; init; }

    [JsonPropertyName("billing_retry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPlanBillingRetry? BillingRetry { get; init; }

    [JsonPropertyName("refund_period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RefundPeriod { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Subscription Detail (Merchant API)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response from GET /projects/{project_id}/subscriptions/{subscription_id}
/// </summary>
public sealed class XsollaSubscriptionDetailResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("charge_amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ChargeAmount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("date_create")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateCreate { get; init; }

    [JsonPropertyName("date_end")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateEnd { get; init; }

    [JsonPropertyName("date_last_charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateLastCharge { get; init; }

    [JsonPropertyName("date_next_charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateNextCharge { get; init; }

    [JsonPropertyName("comment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; init; }

    [JsonPropertyName("plan")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaSubscriptionPlanResponse? Plan { get; init; }

    [JsonPropertyName("product")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaSubscriptionProductWithId? Product { get; init; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaSubscriptionUser? User { get; init; }
}

public sealed class XsollaSubscriptionUser
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Subscription Update
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request for PUT /projects/{project_id}/users/{user_id}/subscriptions/{subscription_id}
/// </summary>
public sealed class XsollaSubscriptionUpdateRequest
{
    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("cancel_subscription_payment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CancelSubscriptionPayment { get; init; }

    [JsonPropertyName("timeshift")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTimeshift? Timeshift { get; init; }
}

public sealed class XsollaTimeshift
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Management Subscriptions (JWT-auth endpoints)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response from GET /api/user/v1/management/projects/{project_id}/subscriptions
/// </summary>
public sealed class XsollaManagementSubscriptionListResponse
{
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaManagementSubscriptionItem[]? Items { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }
}

/// <summary>
/// Subscription item in the management list endpoint.
/// </summary>
public sealed class XsollaManagementSubscriptionItem
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("plan_name")]
    public required string PlanName { get; init; }

    [JsonPropertyName("plan_description")]
    public required string PlanDescription { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("is_in_trial")]
    public bool IsInTrial { get; init; }

    [JsonPropertyName("trial_period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TrialPeriod { get; init; }

    [JsonPropertyName("date_create")]
    public required string DateCreate { get; init; }

    [JsonPropertyName("date_last_charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateLastCharge { get; init; }

    [JsonPropertyName("date_next_charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateNextCharge { get; init; }

    [JsonPropertyName("charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCharge? Charge { get; init; }

    [JsonPropertyName("period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPeriod? Period { get; init; }

    [JsonPropertyName("product_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProductName { get; init; }

    [JsonPropertyName("product_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProductDescription { get; init; }

    [JsonPropertyName("payment_account")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPaymentRecurrentAccount? PaymentAccount { get; init; }
}

/// <summary>
/// Full single management subscription from GET /api/user/v1/management/.../subscriptions/{subscription_id}
/// </summary>
public sealed class XsollaManagementSubscriptionResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("plan_name")]
    public required string PlanName { get; init; }

    [JsonPropertyName("plan_description")]
    public required string PlanDescription { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("is_in_trial")]
    public bool IsInTrial { get; init; }

    [JsonPropertyName("trial_period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TrialPeriod { get; init; }

    [JsonPropertyName("date_create")]
    public required string DateCreate { get; init; }

    [JsonPropertyName("date_end")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateEnd { get; init; }

    [JsonPropertyName("date_last_charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateLastCharge { get; init; }

    [JsonPropertyName("date_next_charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateNextCharge { get; init; }

    [JsonPropertyName("charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCharge? Charge { get; init; }

    [JsonPropertyName("period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPeriod? Period { get; init; }

    [JsonPropertyName("product_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProductName { get; init; }

    [JsonPropertyName("product_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProductDescription { get; init; }

    [JsonPropertyName("is_renew_possible")]
    public bool IsRenewPossible { get; init; }

    [JsonPropertyName("is_change_to_non_renew_possible")]
    public bool IsChangeToNonRenewPossible { get; init; }

    [JsonPropertyName("is_change_plan_allowed")]
    public bool IsChangePlanAllowed { get; init; }

    [JsonPropertyName("last_successful_charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaLastSuccessfulCharge? LastSuccessfulCharge { get; init; }

    [JsonPropertyName("payment_account")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPaymentRecurrentAccount? PaymentAccount { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Management Settings
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response from GET /api/user/v1/management/projects/{project_id}/subscriptions/settings
/// </summary>
public sealed class XsollaManagementSettingsResponse
{
    [JsonPropertyName("recurrent_cancel_possible")]
    public bool RecurrentCancelPossible { get; init; }

    [JsonPropertyName("allow_change_package")]
    public bool AllowChangePackage { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Management — Change Plan
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Plan available for change from GET .../subscriptions/{id}/plans_for_change
/// </summary>
public sealed class XsollaChangePlanListResponse
{
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaChangePlanItem[]? Items { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }
}

public sealed class XsollaChangePlanItem
{
    [JsonPropertyName("plan_id")]
    public int PlanId { get; init; }

    [JsonPropertyName("plan_external_id")]
    public required string PlanExternalId { get; init; }

    [JsonPropertyName("plan_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanType { get; init; }

    [JsonPropertyName("plan_name")]
    public required string PlanName { get; init; }

    [JsonPropertyName("plan_description")]
    public required string PlanDescription { get; init; }

    [JsonPropertyName("plan_start_date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanStartDate { get; init; }

    [JsonPropertyName("plan_end_date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanEndDate { get; init; }

    [JsonPropertyName("plan_group_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanGroupId { get; init; }

    [JsonPropertyName("trial_period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TrialPeriod { get; init; }

    [JsonPropertyName("period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPeriod? Period { get; init; }

    [JsonPropertyName("charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCharge? Charge { get; init; }

    [JsonPropertyName("promotion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPromotion? Promotion { get; init; }
}

/// <summary>
/// Response from POST .../subscriptions/{id}/change_plan
/// </summary>
public sealed class XsollaChangePlanLinkResponse
{
    [JsonPropertyName("link_to_ps")]
    public required string LinkToPs { get; init; }
}

/// <summary>
/// Detailed plan-for-change from GET .../subscriptions/{id}/plans_for_change/{plan_id}
/// </summary>
public sealed class XsollaChangePlanDetailResponse
{
    [JsonPropertyName("plan_id")]
    public int PlanId { get; init; }

    [JsonPropertyName("plan_external_id")]
    public required string PlanExternalId { get; init; }

    [JsonPropertyName("plan_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanType { get; init; }

    [JsonPropertyName("plan_name")]
    public required string PlanName { get; init; }

    [JsonPropertyName("plan_description")]
    public required string PlanDescription { get; init; }

    [JsonPropertyName("plan_start_date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanStartDate { get; init; }

    [JsonPropertyName("plan_end_date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanEndDate { get; init; }

    [JsonPropertyName("plan_group_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanGroupId { get; init; }

    [JsonPropertyName("trial_period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TrialPeriod { get; init; }

    [JsonPropertyName("period")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPeriod? Period { get; init; }

    [JsonPropertyName("charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCharge? Charge { get; init; }

    [JsonPropertyName("promotion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPromotion? Promotion { get; init; }

    [JsonPropertyName("payment_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaSubscriptionPaymentDetails? PaymentDetails { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Management — Payment Account
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response from GET .../subscriptions/{id}/payment_account
/// </summary>
public sealed class XsollaManagementPaymentAccountResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("ps_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PsName { get; init; }

    [JsonPropertyName("switch_icon_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SwitchIconName { get; init; }

    [JsonPropertyName("card_expiry_date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCardExpiryDate? CardExpiryDate { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Products
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Subscription product from GET /projects/{project_id}/subscriptions/products
/// </summary>
public sealed class XsollaSubscriptionProduct
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("group_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaLocalizedStrings? Description { get; init; }
}

public sealed class XsollaSubscriptionProductWithId
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("group_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaLocalizedStrings? Description { get; init; }
}

/// <summary>
/// Request for POST/PUT /projects/{project_id}/subscriptions/products
/// </summary>
public sealed class XsollaSubscriptionProductRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("group_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaLocalizedStrings? Description { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Payments
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Payment item from GET /projects/{project_id}/subscriptions/payments
/// </summary>
public sealed class XsollaSubscriptionPayment
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("subscription_id")]
    public int SubscriptionId { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Date { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Currencies
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Currency item from GET /projects/{project_id}/subscriptions/currencies
/// </summary>
public sealed class XsollaSubscriptionCurrency
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Coupons
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response from GET /projects/{project_id}/coupons/{code}/details
/// </summary>
public sealed class XsollaCouponInfoResponse
{
    [JsonPropertyName("coupon_id")]
    public int CouponId { get; init; }

    [JsonPropertyName("coupon_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CouponCode { get; init; }

    [JsonPropertyName("campaign_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CampaignCode { get; init; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }

    [JsonPropertyName("project_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProjectId { get; init; }

    [JsonPropertyName("expiration_date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpirationDate { get; init; }

    [JsonPropertyName("redeems_count_remain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RedeemsCountRemain { get; init; }

    [JsonPropertyName("redeems_count_for_user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RedeemsCountForUser { get; init; }

    [JsonPropertyName("virtual_currency_amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VirtualCurrencyAmount { get; init; }

    [JsonPropertyName("virtual_items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? VirtualItems { get; init; }

    [JsonPropertyName("subscription_coupon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCouponSubscription? SubscriptionCoupon { get; init; }
}

public sealed class XsollaCouponSubscription
{
    [JsonPropertyName("plan_id")]
    public int PlanId { get; init; }

    [JsonPropertyName("product_id")]
    public int ProductId { get; init; }

    [JsonPropertyName("trial_period")]
    public int TrialPeriod { get; init; }
}

/// <summary>
/// Request for POST /projects/{project_id}/coupons/{code}/redeem
/// </summary>
public sealed class XsollaCouponRedeemRequest
{
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }
}

/// <summary>
/// Response from POST /projects/{project_id}/coupons/{code}/redeem
/// </summary>
public sealed class XsollaCouponRedeemResponse
{
    [JsonPropertyName("virtual_currency_amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VirtualCurrencyAmount { get; init; }

    [JsonPropertyName("virtual_items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? VirtualItems { get; init; }

    [JsonPropertyName("subscription_coupon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCouponSubscription? SubscriptionCoupon { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Merchant Subscription List
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response from GET /merchants/{merchant_id}/subscriptions
/// </summary>
public sealed class XsollaMerchantSubscriptionListResponse
{
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaMerchantSubscriptionItem[]? Items { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }
}

public sealed class XsollaMerchantSubscriptionItem
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("user_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; init; }

    [JsonPropertyName("plan_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlanId { get; init; }

    [JsonPropertyName("product_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProductId { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("charge_amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ChargeAmount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("date_create")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateCreate { get; init; }

    [JsonPropertyName("date_end")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateEnd { get; init; }

    [JsonPropertyName("date_last_charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateLastCharge { get; init; }

    [JsonPropertyName("date_next_charge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateNextCharge { get; init; }
}
