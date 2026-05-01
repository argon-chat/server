namespace Argon.Core.Features.Integrations.Xsolla;

using System.Diagnostics;
using System.Diagnostics.Metrics;

public static class XsollaInstruments
{
    private static readonly Meter Meter = Instruments.Meter;

    public static readonly ActivitySource ActivitySource = new("Argon.Xsolla");

    // ── Webhooks ────────────────────────────────────────────────────────

    public static readonly Counter<long> WebhooksReceived = Meter.CreateCounter<long>(
        InstrumentNames.XsollaWebhooksReceived,
        description: "Total number of Xsolla webhooks received. Tags: type, status");

    public static readonly Counter<long> WebhookErrors = Meter.CreateCounter<long>(
        InstrumentNames.XsollaWebhookErrors,
        description: "Total number of Xsolla webhook processing errors. Tags: type, error");

    public static readonly Histogram<double> WebhookDuration = Meter.CreateHistogram<double>(
        InstrumentNames.XsollaWebhookDuration,
        unit: "ms",
        description: "Duration of Xsolla webhook processing. Tags: type");

    public static readonly Counter<long> WebhookSignatureFailures = Meter.CreateCounter<long>(
        InstrumentNames.XsollaWebhookSignatureFailures,
        description: "Total number of Xsolla webhook signature validation failures");

    // ── Payments ────────────────────────────────────────────────────────

    public static readonly Counter<long> PaymentsProcessed = Meter.CreateCounter<long>(
        InstrumentNames.XsollaPaymentsProcessed,
        description: "Total number of payments processed. Tags: type (subscription, boost_pack, gift)");

    public static readonly Counter<long> RefundsProcessed = Meter.CreateCounter<long>(
        InstrumentNames.XsollaRefundsProcessed,
        description: "Total number of refunds processed. Tags: type (refund, partial_refund, order_canceled)");

    public static readonly Counter<decimal> PaymentRevenue = Meter.CreateCounter<decimal>(
        InstrumentNames.XsollaPaymentRevenue,
        unit: "{currency_unit}",
        description: "Total payment revenue amount. Tags: currency, type");

    // ── Subscriptions ───────────────────────────────────────────────────

    public static readonly Counter<long> SubscriptionsCreated = Meter.CreateCounter<long>(
        InstrumentNames.XsollaSubscriptionsCreated,
        description: "Total number of subscriptions created. Tags: plan");

    public static readonly Counter<long> SubscriptionsCanceled = Meter.CreateCounter<long>(
        InstrumentNames.XsollaSubscriptionsCanceled,
        description: "Total number of subscriptions canceled. Tags: plan");

    // ── Boosts ──────────────────────────────────────────────────────────

    public static readonly Counter<long> BoostsGranted = Meter.CreateCounter<long>(
        InstrumentNames.XsollaBoostsGranted,
        description: "Total number of boosts granted. Tags: plan, count");

    // ── Checkout / API calls ────────────────────────────────────────────

    public static readonly Counter<long> CheckoutsCreated = Meter.CreateCounter<long>(
        InstrumentNames.XsollaCheckoutsCreated,
        description: "Total number of checkout sessions created. Tags: type");

    public static readonly Histogram<double> ApiCallDuration = Meter.CreateHistogram<double>(
        InstrumentNames.XsollaApiCallDuration,
        unit: "ms",
        description: "Duration of Xsolla API calls. Tags: endpoint, status");

    public static readonly Counter<long> ApiCallErrors = Meter.CreateCounter<long>(
        InstrumentNames.XsollaApiCallErrors,
        description: "Total number of Xsolla API call errors. Tags: endpoint, status_code");
}
