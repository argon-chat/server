/**
 * The Argon server dashboards in Sentry, as code.
 *
 * Every widget reads the trace metrics the server sends through
 * `src/Argon.Core/Features/Sentry/SentryMeterBridge.cs`, which forwards the
 * `System.Diagnostics.Metrics` meters named in `Sentry:Metrics:Meters` — today `Argon`, `Ion`,
 * `Microsoft.Orleans.*`, `Microsoft.AspNetCore.*`, `System.Runtime` and `System.Net.Http`. Edit
 * here, then re-run: a dashboard with the same title is replaced, so this file is the source of
 * truth, not what was clicked together in the UI.
 *
 *   bun scipts/sentry/dashboards.ts --dry        print counts and a sample widget, create nothing
 *   bun scipts/sentry/dashboards.ts --validate   run every widget through the validation endpoint
 *   bun scipts/sentry/dashboards.ts --probe      ask the events API whether each widget has data
 *   bun scipts/sentry/dashboards.ts --create     create/replace the dashboards (ids change; links printed)
 *   bun scipts/sentry/dashboards.ts --dump       print the dashboards as the JSON the API receives
 *
 * `--only <substring>` narrows any of those to the dashboards whose title contains it.
 *
 * ── Which Sentry, and whose ─────────────────────────────────────────────────────────────────────
 * Nothing here names an instance, an organisation or a project. This repository is public and those
 * three are the deployment's, not the code's: an installation address plus an org slug is a map of
 * where to point an attack, and a project id belongs to whoever runs the server rather than to
 * anyone who reads it. They come from the environment, or — when a person is running this by hand —
 * from a prompt:
 *
 *   SENTRY_URL       the installation, e.g. https://sentry.example.com (a trailing /api/0 is fine)
 *   SENTRY_ORG       organisation slug
 *   SENTRY_PROJECT   numeric project id the server reports to
 *   SENTRY_AUTH_TOKEN  scopes org:read, org:write, project:read
 *
 * The token is the one exception to the prompt: it is only ever read from the environment, because
 * a prompt echoes what is typed and a shell has a history. It is never read from a file in the repo
 * and never printed.
 *
 * ── How a trace-metric widget is encoded ────────────────────────────────────────────────────────
 * The widget editor relies on every part of this:
 *   - the metric lives INSIDE the aggregate: `sum(value,<name>,<type>,<unit>)`,
 *     `p95(value,Argon.argon_redis_operation_duration,distribution,millisecond)`,
 *     `equation|avg(value,System.Runtime.dotnet.process.memory.working_set,gauge,none) / 1048576`;
 *   - <type> is counter | gauge | distribution and <unit> must be exactly what Sentry stored —
 *     a wrong unit silently returns null rather than an error;
 *   - `conditions` carries attribute filters only (`result:success`), never `metric.name:`.
 * Other rules the server enforces: displayType ∈ line/area/bar/big_number/categorical_bar/heatmap;
 * grouping needs a widget-level `limit` (max 10); at most one equation per timeseries widget; fewer
 * than 30 widgets per dashboard.
 *
 * ── Names, types and units, and why they are what they are ──────────────────────────────────────
 * The bridge sends `<meter name>.<instrument name>` and Sentry rewrites `-` to `_`, so
 * `argon-redis-operations` on meter `Argon` arrives as `Argon.argon_redis_operations`.
 *
 * The type is decided by `SentryMeterBridge.MetricTypeFor`: `Histogram<T>` → distribution, every
 * `ObservableInstrument<T>` → gauge, everything else → counter. So `Counter<T>`, `UpDownCounter<T>`
 * and .NET 9's synchronous `Gauge<T>` all arrive as *counters* — see the UpDownCounter note below.
 *
 * The unit is `instrument.Unit` as Sentry understood it, and Sentry only understands a short list:
 * `ms` → millisecond, `s` → second, and everything else — `By`, `{connection}`, `activations`, or
 * no unit at all — is stored as `none`. Counters never carry a unit, because the bridge's
 * `EmitCounter` does not take one. Hence: no `byte` unit anywhere below, even for the metrics whose
 * values are bytes.
 *
 * ── UpDownCounters do not measure a level ───────────────────────────────────────────────────────
 * ASP.NET Core and Orleans report "how many are active right now" with `UpDownCounter`, which
 * publishes +1 on the way in and -1 on the way out. Those reach Sentry as counter increments, so
 * `sum` over an interval is the NET CHANGE in that interval, not the number active — and
 * `count` is the number of enter/exit events. Widgets built on them say so in their description and
 * are named for what they actually show. Where a real level was wanted, prefer a duration
 * histogram's `count` (one measurement per completed connection/request) or one of the observable
 * gauges Orleans and the runtime publish.
 */

const COLS = 6;
/** Per-request budget for the Sentry API: a request that never answers aborts instead of hanging the run. */
const API_TIMEOUT_MS = 60_000;

const argv = process.argv.slice(2);
const has = (flag: string) => argv.includes(flag);
const onlyFilter = (() => {
  const i = argv.indexOf("--only");
  return i >= 0 ? argv[i + 1] : undefined;
})();

// ───────────────────────────── where to point this ─────────────────────────────
// See the header: the instance, the org and the project id are the deployment's, so they are asked
// for rather than written down. Resolved here, at module scope, because the dashboards embed the
// project id — even `--dry` needs to know it.

/**
 * A setting from the environment, or typed in when someone is running this by hand. Exits rather
 * than guessing: a wrong org silently builds dashboards for somebody else's Sentry.
 */
function setting(name: string, question: string): string {
  const fromEnv = process.env[name]?.trim();
  if (fromEnv)
    return fromEnv;

  const typed = process.stdin.isTTY ? prompt(question)?.trim() : undefined;
  if (typed)
    return typed;

  console.error(`${name} is not set, and there is no terminal to ask on. See the header of this file.`);
  process.exit(2);
}

/** `https://sentry.example.com`, with any trailing slash or `/api/0` the caller pasted taken back off. */
const HOST = setting("SENTRY_URL", "Sentry URL (https://sentry.example.com): ")
  .replace(/\/+$/, "")
  .replace(/\/api\/0$/, "");
const ORG = setting("SENTRY_ORG", "Organisation slug: ");
const PROJECT = Number(setting("SENTRY_PROJECT", "Project id (numeric): "));
const BASE = `${HOST}/api/0`;

if (!/^https?:\/\/[^/]+$/.test(HOST)) {
  console.error(`SENTRY_URL is not an installation address: "${HOST}"`);
  process.exit(2);
}
if (!Number.isInteger(PROJECT) || PROJECT <= 0) {
  console.error("SENTRY_PROJECT must be the numeric project id, which is not the same as its slug");
  process.exit(2);
}

// ───────────────────────────── metric catalogue ─────────────────────────────
// Type and unit of every metric used below, exactly as Sentry stored it. Confirmed against
// `GET /organizations/<org>/events/?dataset=tracemetrics&project=<id>&field=metric.name&field=metric.type
// &field=metric.unit&field=count(value)`; the entries no traffic has produced yet are derived from
// the instrument declaration in the server and the mapping rules in the header.
// A metric missing here cannot be put on a dashboard.

type MetricType = "counter" | "gauge" | "distribution";
type MetricUnit = "none" | "millisecond" | "second";
type MetricSpec = readonly [type: MetricType, unit: MetricUnit];

/** Counter<T>, UpDownCounter<T> and Gauge<T>: the bridge sends all three unitless. */
const C: MetricSpec = ["counter", "none"];
/** ObservableGauge<T> with no unit Sentry recognises. */
const G: MetricSpec = ["gauge", "none"];
/** ObservableGauge<T> declared in seconds. */
const GS: MetricSpec = ["gauge", "second"];
/** Histogram<T> declared `ms`. */
const MS: MetricSpec = ["distribution", "millisecond"];
/** Histogram<T> declared `s`. */
const S: MetricSpec = ["distribution", "second"];
/** Histogram<T> whose unit Sentry did not recognise — bytes, activations, counts of things. */
const D: MetricSpec = ["distribution", "none"];

const METRICS = {
  // ── Argon: Redis pool and cache (attrs: operation, result, profile) ────────────────────────
  "Argon.argon_redis_operations": C,
  "Argon.argon_redis_operation_duration": MS,
  "Argon.argon_redis_operation_retries": C,
  "Argon.argon_redis_distributed_cache_operations": C,
  "Argon.argon_redis_distributed_cache_operation_duration": MS,
  "Argon.argon_redis_key_expiration_events": C,
  "Argon.argon_redis_connections_allocated": C,
  "Argon.argon_redis_connections_deallocated": C,
  "Argon.argon_redis_connections_rented": C,
  "Argon.argon_redis_connections_returned": C,
  "Argon.argon_redis_connections_returned_faulted": C,
  "Argon.argon_redis_connections_taken": G,
  "Argon.argon_redis_connections_total": G,
  "Argon.argon_redis_pool_max_size": G,
  "Argon.argon_redis_pool_cleanups": C,
  "Argon.argon_redis_pool_connections_removed": C,
  "Argon.argon_redis_pool_scale_ups": C,

  // ── Argon: sessions and presence (attrs: from_status, to_status) ───────────────────────────
  "Argon.argon_user_online_count": G,
  "Argon.argon_user_sessions_active": G,
  "Argon.argon_user_sessions_started": C,
  "Argon.argon_user_sessions_ended": C,
  "Argon.argon_user_session_heartbeats": C,
  "Argon.argon_user_session_expirations": C,
  "Argon.argon_user_session_duration": S,
  "Argon.argon_user_status_changes": C,

  // ── Argon: authorization ───────────────────────────────────────────────────────────────────
  "Argon.argon_authorization_attempts": C,
  "Argon.argon_authorization_duration": MS,
  "Argon.argon_authorization_otp_sent": C,
  "Argon.argon_external_authorization_attempts": C,
  "Argon.argon_user_registrations": C,
  "Argon.argon_user_registration_duration": MS,
  "Argon.argon_password_resets": C,
  "Argon.argon_password_reset_duration": MS,

  // ── Argon: channels, messages and voice (attrs: result on the flushes) ─────────────────────
  "Argon.argon_channel_messages_sent": C,
  "Argon.argon_channel_message_send_duration": MS,
  "Argon.argon_channel_last_message_flushes": C,
  "Argon.argon_channel_last_message_absorbed": C,
  "Argon.argon_channel_typing_events": C,
  "Argon.argon_channel_reactions_added": C,
  "Argon.argon_channel_reactions_removed": C,
  "Argon.argon_channel_member_kicks": C,
  "Argon.argon_channel_voice_joins": C,
  "Argon.argon_channel_voice_leaves": C,
  "Argon.argon_channel_voice_session_duration": S,
  // Gauge<int>, which is synchronous — so the bridge classifies it as a counter. See the header.
  "Argon.argon_channel_voice_active_users": C,
  "Argon.argon_channel_recordings_started": C,
  "Argon.argon_channel_recordings_stopped": C,

  // ── Argon: account deletion and data export (attrs: reason, phase) ─────────────────────────
  "Argon.argon_deletion_requested": C,
  "Argon.argon_deletion_scheduled": C,
  "Argon.argon_deletion_completed": C,
  "Argon.argon_deletion_failed": C,
  "Argon.argon_deletion_cancelled": C,
  "Argon.argon_deletion_rejected": C,
  "Argon.argon_deletion_reminders_sent": C,
  "Argon.argon_deletion_execution_duration": S,
  "Argon.argon_export_requested": C,
  "Argon.argon_export_started": C,
  "Argon.argon_export_completed": C,
  "Argon.argon_export_failed": C,
  "Argon.argon_export_cancelled": C,
  "Argon.argon_export_rate_limited": C,
  "Argon.argon_export_ticks_processed": C,
  "Argon.argon_export_duration": S,
  "Argon.argon_export_tick_duration": MS,
  "Argon.argon_export_archive_size_bytes": D,

  // ── Argon: bot API (attrs: event_type) ─────────────────────────────────────────────────────
  "Argon.argon_bot_events_published": C,
  "Argon.argon_bot_event_publish_duration": MS,
  "Argon.argon_bot_event_publish_errors": C,
  "Argon.argon_bot_sse_connections_opened": C,
  "Argon.argon_bot_sse_connections_closed": C,
  "Argon.argon_bot_sse_connections_active": G,
  "Argon.argon_bot_sse_events_delivered": C,
  "Argon.argon_bot_command_invocations": C,
  "Argon.argon_bot_command_dispatch_duration": MS,
  "Argon.argon_bot_command_errors": C,

  // ── Argon: object storage (attrs: purpose, reason, operation, status, sweep_type) ──────────
  "Argon.argon_storage_uploads_requested": C,
  "Argon.argon_storage_uploads_finalized": C,
  "Argon.argon_storage_uploads_failed": C,
  "Argon.argon_storage_upload_size_bytes": D,
  "Argon.argon_storage_upload_finalize_duration": MS,
  "Argon.argon_storage_presigned_get_generated": C,
  "Argon.argon_storage_public_urls_served": C,
  "Argon.argon_storage_ref_increments": C,
  "Argon.argon_storage_ref_decrements": C,
  "Argon.argon_storage_gc_blobs_swept": C,
  "Argon.argon_storage_gc_orphans_swept": C,
  "Argon.argon_storage_gc_errors": C,
  "Argon.argon_storage_gc_sweep_duration": MS,
  "Argon.argon_storage_s3_operations": C,
  "Argon.argon_storage_s3_operation_duration": MS,
  "Argon.argon_storage_active_blobs": C,
  "Argon.argon_storage_total_stored_bytes": C,

  // ── Argon: content moderation (attrs: purpose, action, stage) ──────────────────────────────
  "Argon.argon_moderation_evaluations_total": C,
  "Argon.argon_moderation_rejections_total": C,
  "Argon.argon_moderation_evaluations_skipped": C,
  "Argon.argon_moderation_violations_recorded": C,
  "Argon.argon_moderation_evaluation_duration": MS,
  "Argon.argon_moderation_s3_download_duration": MS,
  "Argon.argon_moderation_inference_duration": MS,
  "Argon.argon_moderation_active_inferences": C,

  // ── Argon: phone verification (attrs: provider, result) ────────────────────────────────────
  "Argon.argon_phone_verification_sent": C,
  "Argon.argon_phone_verification_checks": C,
  "Argon.argon_phone_verification_send_duration": MS,
  "Argon.argon_phone_verification_check_duration": MS,
  "Argon.argon_phone_verification_fallbacks": C,
  "Argon.argon_phone_verification_cost": C,
  "Argon.argon_phone_telegram_send_ability_checks": C,
  // Gauge<decimal>, synchronous — a counter to the bridge. See the header.
  "Argon.argon_phone_telegram_balance": C,

  // ── Argon: Xsolla billing (attrs: type, status, plan, endpoint, currency) ──────────────────
  "Argon.argon_xsolla_webhooks_received": C,
  "Argon.argon_xsolla_webhook_errors": C,
  "Argon.argon_xsolla_webhook_duration": MS,
  "Argon.argon_xsolla_webhook_signature_failures": C,
  "Argon.argon_xsolla_payments_processed": C,
  "Argon.argon_xsolla_refunds_processed": C,
  "Argon.argon_xsolla_payment_revenue": C,
  "Argon.argon_xsolla_subscriptions_created": C,
  "Argon.argon_xsolla_subscriptions_canceled": C,
  "Argon.argon_xsolla_boosts_granted": C,
  "Argon.argon_xsolla_checkouts_created": C,
  "Argon.argon_xsolla_api_call_duration": MS,
  "Argon.argon_xsolla_api_call_errors": C,

  // ── Argon: TTL sweeper, rebalancing, regions (attrs: outcome, table, status, reason) ───────
  "Argon.argon_ttl_sweep_passes": C,
  "Argon.argon_ttl_sweep_rows": C,
  "Argon.argon_ttl_sweep_duration": MS,
  "Argon.argon_ttl_sweep_backlog": G,
  "Argon.argon_orleans_rebalance_checks": C,
  "Argon.argon_orleans_rebalance_accepted": C,
  "Argon.argon_orleans_rebalance_rejected": C,
  "Argon.argon_orleans_imbalance_value": D,
  "Argon.argon_region_foreign_calls": C,

  // ── Ion: the RPC transport ─────────────────────────────────────────────────────────────────
  "Ion.ion_rpc_request_total": C,
  "Ion.ion_rpc_request_duration": MS,
  "Ion.ion_rpc_request_error": C,

  // ── ASP.NET Core: ingress ──────────────────────────────────────────────────────────────────
  // attrs: http.route, http.request.method, tags[http.response.status_code,number], error.type
  "Microsoft.AspNetCore.Hosting.http.server.request.duration": S,
  "Microsoft.AspNetCore.Hosting.http.server.active_requests": C,
  "Microsoft.AspNetCore.Routing.aspnetcore.routing.match_attempts": C,
  "Microsoft.AspNetCore.RateLimiting.aspnetcore.rate_limiting.requests": C,
  "Microsoft.AspNetCore.RateLimiting.aspnetcore.rate_limiting.request_lease.duration": S,
  "Microsoft.AspNetCore.RateLimiting.aspnetcore.rate_limiting.active_request_leases": C,
  "Microsoft.AspNetCore.Authentication.aspnetcore.authentication.authenticate.duration": S,
  "Microsoft.AspNetCore.Authentication.aspnetcore.authentication.sign_ins": C,
  "Microsoft.AspNetCore.Authorization.aspnetcore.authorization.attempts": C,
  "Microsoft.AspNetCore.Server.Kestrel.kestrel.connection.duration": S,
  "Microsoft.AspNetCore.Server.Kestrel.kestrel.active_connections": C,
  "Microsoft.AspNetCore.Server.Kestrel.kestrel.queued_connections": C,
  "Microsoft.AspNetCore.Server.Kestrel.kestrel.queued_requests": C,
  "Microsoft.AspNetCore.Server.Kestrel.kestrel.upgraded_connections": C,
  "Microsoft.AspNetCore.Server.Kestrel.kestrel.tls_handshake.duration": S,
  "Microsoft.AspNetCore.Server.Kestrel.kestrel.active_tls_handshakes": C,
  "Microsoft.AspNetCore.Http.Connections.signalr.server.active_connections": C,
  "Microsoft.AspNetCore.Http.Connections.signalr.server.connection.duration": S,
  "Microsoft.AspNetCore.MemoryPool.aspnetcore.memory_pool.rented": C,
  "Microsoft.AspNetCore.MemoryPool.aspnetcore.memory_pool.pooled": C,
  "Microsoft.AspNetCore.MemoryPool.aspnetcore.memory_pool.allocated": C,
  "Microsoft.AspNetCore.MemoryPool.aspnetcore.memory_pool.evicted": C,

  // ── System.Net.Http: what this process calls out to ────────────────────────────────────────
  // attrs: server.address, http.request.method, tags[http.response.status_code,number], error.type
  "System.Net.Http.http.client.request.duration": S,
  "System.Net.Http.http.client.request.time_in_queue": S,
  "System.Net.Http.http.client.connection.duration": S,
  "System.Net.Http.http.client.active_requests": C,
  "System.Net.Http.http.client.open_connections": C,

  // ── System.Runtime (attrs: gc.heap.generation, cpu.mode) ───────────────────────────────────
  "System.Runtime.dotnet.gc.collections": G,
  "System.Runtime.dotnet.gc.pause.time": GS,
  "System.Runtime.dotnet.gc.heap.total_allocated": G,
  "System.Runtime.dotnet.gc.last_collection.heap.size": G,
  "System.Runtime.dotnet.gc.last_collection.heap.fragmentation.size": G,
  "System.Runtime.dotnet.gc.last_collection.memory.committed_size": G,
  "System.Runtime.dotnet.process.memory.working_set": G,
  "System.Runtime.dotnet.process.cpu.time": GS,
  "System.Runtime.dotnet.process.cpu.count": G,
  "System.Runtime.dotnet.thread_pool.thread.count": G,
  "System.Runtime.dotnet.thread_pool.queue.length": G,
  "System.Runtime.dotnet.thread_pool.work_item.count": G,
  "System.Runtime.dotnet.monitor.lock_contentions": G,
  "System.Runtime.dotnet.timer.count": G,
  "System.Runtime.dotnet.assembly.count": G,
  "System.Runtime.dotnet.exceptions": C,
  "System.Runtime.dotnet.jit.compiled_methods": G,
  "System.Runtime.dotnet.jit.compiled_il.size": G,
  "System.Runtime.dotnet.jit.compilation.time": GS,

  // ── Orleans: the silo ──────────────────────────────────────────────────────────────────────
  "Microsoft.Orleans.orleans_grains": C,
  "Microsoft.Orleans.orleans_catalog_activations": G,
  "Microsoft.Orleans.orleans_catalog_activation_working_set": G,
  "Microsoft.Orleans.orleans_catalog_activation_created": C,
  "Microsoft.Orleans.orleans_catalog_activation_destroyed": C,
  "Microsoft.Orleans.orleans_catalog_activation_shutdown": C,
  "Microsoft.Orleans.orleans_catalog_activation_collections": C,
  "Microsoft.Orleans.orleans_catalog_activation_latency": MS,
  "Microsoft.Orleans.orleans_catalog_deactivation_latency": MS,
  "Microsoft.Orleans.orleans_app_requests_latency_bucket": G,
  "Microsoft.Orleans.orleans_app_requests_latency_count": G,
  "Microsoft.Orleans.orleans_app_requests_latency_sum": G,
  "Microsoft.Orleans.orleans_messaging_sent_messages_size": D,
  "Microsoft.Orleans.orleans_messaging_received_messages_size": D,
  "Microsoft.Orleans.orleans_messaging_sent_header_size": G,
  "Microsoft.Orleans.orleans_messaging_received_header_size": G,
  "Microsoft.Orleans.orleans_messaging_sent_local": G,
  "Microsoft.Orleans.orleans_messaging_rerouted": C,
  "Microsoft.Orleans.orleans_messaging_rejected": C,
  "Microsoft.Orleans.orleans_messaging_pings_sent": C,
  "Microsoft.Orleans.orleans_messaging_pings_received": C,
  "Microsoft.Orleans.orleans_messaging_pings_reply_received": C,
  "Microsoft.Orleans.orleans_messaging_processing_dispatcher_received": G,
  "Microsoft.Orleans.orleans_messaging_processing_dispatcher_processed": G,
  "Microsoft.Orleans.orleans_messaging_processing_dispatcher_forwarded": G,
  "Microsoft.Orleans.orleans_messaging_processing_ima_received": G,
  "Microsoft.Orleans.orleans_messaging_processing_ima_enqueued": G,
  "Microsoft.Orleans.orleans_messaging_processing_activation_data": G,
  "Microsoft.Orleans.orleans_directory_lookups_cache_issued": C,
  "Microsoft.Orleans.orleans_directory_lookups_cache_successes": C,
  "Microsoft.Orleans.orleans_directory_cache_size": G,
  "Microsoft.Orleans.orleans_directory_partition_size": G,
  "Microsoft.Orleans.orleans_directory_ring_size": G,
  "Microsoft.Orleans.orleans_directory_ring_local_portion_distance": G,
  "Microsoft.Orleans.orleans_directory_ring_local_portion_percentage": G,
  "Microsoft.Orleans.orleans_directory_ring_local_portion_average_percentage": G,
  "Microsoft.Orleans.orleans_directory_registrations": C,
  "Microsoft.Orleans.orleans_directory_registration_duration": MS,
  "Microsoft.Orleans.orleans_directory_range_lock_held_duration": D,
  "Microsoft.Orleans.orleans_directory_snapshot_transfer_count": C,
  "Microsoft.Orleans.orleans_directory_snapshot_transfer_duration": D,
  "Microsoft.Orleans.orleans_gateway_sent": C,
  "Microsoft.Orleans.orleans_gateway_received": C,
  "Microsoft.Orleans.orleans_gateway_connected_clients": C,
  "Microsoft.Orleans.orleans_client_connected_gateways": G,
  "Microsoft.Orleans.orleans_consistent_ring_size": G,
  "Microsoft.Orleans.orleans_consistent_ring_range_percentage_local": G,
  "Microsoft.Orleans.orleans_consistent_ring_range_percentage_average": G,
  "Microsoft.Orleans.orleans_runtime_available_memory": G,
  "Microsoft.Orleans.orleans_runtime_total_physical_memory": G,
  "Microsoft.Orleans.orleans_reminders_active": G,
  "Microsoft.Orleans.orleans_reminders_ticks_delivered": C,
  "Microsoft.Orleans.orleans_reminders_tardiness": D,
  "Microsoft.Orleans.orleans_watchdog_health_checks": C,
  "Microsoft.Orleans.orleans_networking_sockets_opened": C,
  "Microsoft.Orleans.orleans_networking_sockets_closed": C,
  "Microsoft.Orleans.orleans_storage_read_latency": MS,
  "Microsoft.Orleans.orleans_storage_write_latency": MS,
} as const satisfies Record<string, MetricSpec>;

type MetricName = keyof typeof METRICS;

/** A metric plus the attribute filters to apply to it. */
interface MetricRef {
  name: MetricName;
  conditions: string;
}

/** `M("Argon.argon_redis_operations", "result:failed")` */
const M = (name: MetricName, conditions = ""): MetricRef => ({ name, conditions });

/**
 * `F(M("Argon.argon_redis_operation_duration"), "p95")`
 *   → `p95(value,Argon.argon_redis_operation_duration,distribution,millisecond)`.
 * `F(m, "count_unique(user.id)")` → `count_unique(user.id,<name>,<type>,<unit>)`.
 */
function F(metric: MetricRef, aggregate: string): string {
  const [type, unit] = METRICS[metric.name];
  const fn = aggregate.includes("(") ? aggregate.replace(/\)$/, "") : `${aggregate}(value`;
  return `${fn},${metric.name},${type},${unit})`;
}

// ───────────────────────────── widget model ─────────────────────────────

type DisplayType = "line" | "area" | "bar" | "big_number" | "categorical_bar" | "heatmap";

interface WidgetQuery {
  name: string;
  conditions: string;
  fields: string[];
  aggregates: string[];
  columns: string[];
  orderby: string;
}

interface WidgetDraft {
  title: string;
  displayType: DisplayType;
  widgetType: "tracemetrics";
  queries: WidgetQuery[];
  description?: string;
  interval?: string;
  limit?: number;
  /** Grid size, consumed by layout(). */
  _w: number;
  _h: number;
}

interface Widget extends Omit<WidgetDraft, "_w" | "_h"> {
  layout: { x: number; y: number; w: number; h: number; minH: number };
}

interface Dashboard {
  title: string;
  widgets: Widget[];
  projects: number[];
  period: string;
  environment: string[];
  filters: Record<string, unknown>;
}

interface WidgetOptions {
  w?: number;
  h?: number;
  limit?: number;
  interval?: string;
  description?: string;
}

/**
 * One query line. `aggregates` are short names (`sum`, `p95`, `avg`) or full equations
 * (`equation|<full functions>`); everything else is expanded through F().
 */
function q(
  metric: MetricRef,
  aggregates: string[],
  { columns = [], name = "", orderby }: { columns?: string[]; name?: string; orderby?: string } = {},
): WidgetQuery {
  const full = aggregates.map((a) => (a.startsWith("equation|") ? a : F(metric, a)));
  return {
    name,
    conditions: metric.conditions,
    fields: [...columns, ...full],
    aggregates: full,
    columns,
    orderby: orderby ?? (columns.length ? `-${full[0]}` : ""),
  };
}

function widget(
  title: string,
  displayType: DisplayType,
  queries: WidgetQuery[],
  { w = 2, h = 2, limit, interval = "1h", description }: WidgetOptions = {},
): WidgetDraft {
  const grouped = queries.some((x) => x.columns.length > 0);
  const out: WidgetDraft = { title, displayType, widgetType: "tracemetrics", queries, _w: w, _h: h };
  if (description) out.description = description;
  if (displayType !== "big_number") out.interval = interval;
  if (grouped) out.limit = limit ?? 10;
  return out;
}

/** Big number. `aggregate` is a short name, or a function of `F(metric, …)` for an equation. */
const big = (
  title: string,
  metric: MetricRef,
  aggregate: string | ((f: (agg: string) => string) => string) = "sum",
  description?: string,
) => {
  const agg = typeof aggregate === "function" ? `equation|${aggregate((a) => F(metric, a))}` : aggregate;
  return widget(title, "big_number", [q(metric, [agg])], { w: 1, h: 1, description });
};
const line = (title: string, queries: WidgetQuery[], opts?: WidgetOptions) => widget(title, "line", queries, opts);
const area = (title: string, queries: WidgetQuery[], opts?: WidgetOptions) => widget(title, "area", queries, opts);
/** Categorical bar: one bar per value of `column`. */
const bars = (title: string, metric: MetricRef, column: string, aggregate = "sum", opts: WidgetOptions = {}) =>
  widget(title, "categorical_bar", [q(metric, [aggregate], { columns: [column] })], opts);
/** Line chart with one series per value of `column`. */
const by = (title: string, metric: MetricRef, column: string, aggregate = "sum", opts: WidgetOptions = {}) =>
  line(title, [q(metric, [aggregate], { columns: [column] })], opts);
/** Several metrics/filters as named series on one chart. */
const series = (
  title: string,
  entries: Array<[name: string, metric: MetricRef, aggregate?: string]>,
  opts?: WidgetOptions,
) => line(title, entries.map(([name, metric, aggregate = "sum"]) => q(metric, [aggregate], { name })), opts);
const percentiles = (title: string, metric: MetricRef, opts: WidgetOptions = {}) =>
  line(title, [q(metric, ["p50", "p95"])], opts);

/** Flow the widgets into the 6-column grid, row by row. */
function layout(widgets: WidgetDraft[]): Widget[] {
  let x = 0;
  let y = 0;
  let rowH = 0;
  return widgets.map(({ _w, _h, ...rest }) => {
    if (x + _w > COLS) {
      x = 0;
      y += rowH;
      rowH = 0;
    }
    const placed: Widget = { ...rest, layout: { x, y, w: _w, h: _h, minH: _h } };
    x += _w;
    rowH = Math.max(rowH, _h);
    return placed;
  });
}

// ───────────────────────────── shorthands for the long metric names ─────────────────────────────

const HTTP_IN = "Microsoft.AspNetCore.Hosting.http.server.request.duration" as const;
const HTTP_OUT = "System.Net.Http.http.client.request.duration" as const;
const STATUS = "tags[http.response.status_code,number]";

// ───────────────────────────── dashboards ─────────────────────────────

/** Ingress: what the outside world asks of this server and how it answered. */
const API: WidgetDraft[] = [
  big("Requests", M(HTTP_IN), "count", "One measurement per completed HTTP request"),
  big("p50 (s)", M(HTTP_IN), "p50"),
  big("p95 (s)", M(HTTP_IN), "p95"),
  big("Server errors", M(HTTP_IN, `${STATUS}:>=500`), "count", "Requests answered 5xx"),
  big("Client errors", M(HTTP_IN, `${STATUS}:>=400 ${STATUS}:<500`), "count"),
  big("Connections", M("Microsoft.AspNetCore.Server.Kestrel.kestrel.connection.duration"), "count", "Kestrel connections that closed"),

  line("Request latency (s)", [q(M(HTTP_IN), ["p50", "p95"])], { w: 3 }),
  by("Requests by status code", M(HTTP_IN), STATUS, "count", {
    w: 3,
    description: "Top 10 response codes; a 5xx line appearing at all is the thing to look at",
  }),
  line("p95 by route (s)", [q(M(HTTP_IN), ["p95"], { columns: ["http.route"] })], {
    w: 3,
    description: "The ten slowest routes at the 95th percentile",
  }),
  bars("Busiest routes", M(HTTP_IN), "http.route", "count", { w: 3 }),
  line("5xx by route", [q(M(HTTP_IN, `${STATUS}:>=500`), ["count"], { columns: ["http.route"] })], {
    w: 3,
    description: "Which endpoints are the ones answering 500 and above",
  }),
  by("Requests by method", M(HTTP_IN), "http.request.method", "count", { w: 3 }),

  by("Routing match attempts", M("Microsoft.AspNetCore.Routing.aspnetcore.routing.match_attempts"), "aspnetcore.routing.match_status", "sum", {
    w: 3,
    description: "success / failure — a rising failure line is requests hitting no endpoint",
  }),
  by("Rate limiting", M("Microsoft.AspNetCore.RateLimiting.aspnetcore.rate_limiting.requests"), "aspnetcore.rate_limiting.result", "sum", {
    w: 3,
    description: "acquired / endpoint_limiter / global_limiter / request_canceled",
  }),
  series(
    "Authentication and authorization",
    [
      ["authenticate calls", M("Microsoft.AspNetCore.Authentication.aspnetcore.authentication.authenticate.duration"), "count"],
      ["p95 authenticate (s)", M("Microsoft.AspNetCore.Authentication.aspnetcore.authentication.authenticate.duration"), "p95"],
      ["sign-ins", M("Microsoft.AspNetCore.Authentication.aspnetcore.authentication.sign_ins")],
      ["authorization attempts", M("Microsoft.AspNetCore.Authorization.aspnetcore.authorization.attempts")],
    ],
    {
      w: 3,
      description:
        "Not broken out by outcome on purpose: Sentry's data scrubber rewrites aspnetcore.authentication.result and aspnetcore.authorization.result to [Filtered], so grouping by them yields one meaningless series",
    },
  ),
  by("Connection close reasons", M("Microsoft.AspNetCore.Server.Kestrel.kestrel.connection.duration"), "error.type", "count", {
    w: 3,
    description: "connection_reset / aborted_by_app / app_shutdown_timeout; the unnamed series is a clean close",
  }),

  series(
    "Kestrel connections",
    [
      ["connections closed", M("Microsoft.AspNetCore.Server.Kestrel.kestrel.connection.duration"), "count"],
      ["TLS handshakes", M("Microsoft.AspNetCore.Server.Kestrel.kestrel.tls_handshake.duration"), "count"],
      ["upgraded (websocket)", M("Microsoft.AspNetCore.Server.Kestrel.kestrel.upgraded_connections"), "sum"],
    ],
    { w: 3, description: "Counted from completed connections, not from the active-connection UpDownCounter" },
  ),
  percentiles("Connection lifetime (s)", M("Microsoft.AspNetCore.Server.Kestrel.kestrel.connection.duration"), { w: 3 }),
  percentiles("TLS handshake (s)", M("Microsoft.AspNetCore.Server.Kestrel.kestrel.tls_handshake.duration"), { w: 3 }),
  by("Connections by HTTP version", M("Microsoft.AspNetCore.Server.Kestrel.kestrel.connection.duration"), "network.protocol.version", "count", {
    w: 3,
    description: "1.1 against 2 — a drop in HTTP/2 usually means something in front is terminating differently",
  }),
  series(
    "Kestrel queue pressure",
    [
      ["queued connections (net)", M("Microsoft.AspNetCore.Server.Kestrel.kestrel.queued_connections"), "sum"],
      ["queued requests (net)", M("Microsoft.AspNetCore.Server.Kestrel.kestrel.queued_requests"), "sum"],
    ],
    { w: 3, description: "UpDownCounters: the net change per interval. Sustained above zero means the queue is growing" },
  ),

  series(
    "SignalR",
    [
      ["connections closed", M("Microsoft.AspNetCore.Http.Connections.signalr.server.connection.duration"), "count"],
      ["active (net change)", M("Microsoft.AspNetCore.Http.Connections.signalr.server.active_connections"), "sum"],
    ],
    { w: 3 },
  ),
  percentiles("SignalR connection lifetime (s)", M("Microsoft.AspNetCore.Http.Connections.signalr.server.connection.duration"), { w: 3 }),

  series(
    "Ion RPC",
    [
      ["requests", M("Ion.ion_rpc_request_total")],
      ["errors", M("Ion.ion_rpc_request_error")],
    ],
    { w: 3, description: "The Ion transport the desktop client talks to" },
  ),
  percentiles("Ion RPC latency (ms)", M("Ion.ion_rpc_request_duration"), { w: 3 }),
];

/** The silo: activations, messaging, the directory, storage and rebalancing. */
const ORLEANS: WidgetDraft[] = [
  big("Activations", M("Microsoft.Orleans.orleans_catalog_activations"), "avg", "Mean live activations per silo over the range"),
  big("Peak activations", M("Microsoft.Orleans.orleans_catalog_activations"), "max"),
  big("Created", M("Microsoft.Orleans.orleans_catalog_activation_created")),
  big("Activation p95 (ms)", M("Microsoft.Orleans.orleans_catalog_activation_latency"), "p95"),
  big("Rejected messages", M("Microsoft.Orleans.orleans_messaging_rejected")),
  big("Rerouted messages", M("Microsoft.Orleans.orleans_messaging_rerouted")),

  series(
    "Activations",
    [
      ["live", M("Microsoft.Orleans.orleans_catalog_activations"), "avg"],
      ["working set", M("Microsoft.Orleans.orleans_catalog_activation_working_set"), "avg"],
    ],
    { w: 3, description: "Working set is the subset Orleans considers recently used; the gap is collection candidates" },
  ),
  series(
    "Activation lifecycle",
    [
      ["created", M("Microsoft.Orleans.orleans_catalog_activation_created")],
      ["shutdown", M("Microsoft.Orleans.orleans_catalog_activation_shutdown")],
      ["destroyed", M("Microsoft.Orleans.orleans_catalog_activation_destroyed")],
      ["collections", M("Microsoft.Orleans.orleans_catalog_activation_collections")],
    ],
    { w: 3 },
  ),
  percentiles("Activation latency (ms)", M("Microsoft.Orleans.orleans_catalog_activation_latency"), { w: 3 }),
  percentiles("Deactivation latency (ms)", M("Microsoft.Orleans.orleans_catalog_deactivation_latency"), { w: 3 }),
  by("Grain population by type", M("Microsoft.Orleans.orleans_grains"), "type", "sum", {
    w: 3,
    description:
      "orleans_grains is an UpDownCounter, so each series is the NET change in live activations of that grain type per interval. A type that stays positive hour after hour is accumulating activations",
  }),
  bars("Grain call latency buckets", M("Microsoft.Orleans.orleans_app_requests_latency_bucket"), "duration", "max", {
    w: 3,
    description: "Orleans' own cumulative histogram of application request latency, one bar per bucket edge",
  }),

  series(
    "Message sizes p95 (bytes)",
    [
      ["sent", M("Microsoft.Orleans.orleans_messaging_sent_messages_size"), "p95"],
      ["received", M("Microsoft.Orleans.orleans_messaging_received_messages_size"), "p95"],
    ],
    { w: 3 },
  ),
  line("Message size p95 by connection (bytes)", [q(M("Microsoft.Orleans.orleans_messaging_sent_messages_size"), ["p95"], { columns: ["ConnectionDirection"] })], {
    w: 3,
    description: "SiloToSilo / ClientToGateway / GatewayToClient",
  }),
  by("Rejected messages by direction", M("Microsoft.Orleans.orleans_messaging_rejected"), "Direction", "sum", {
    w: 3,
    description: "Request or OneWay: a message the target refused rather than dropped",
  }),
  series(
    "Silo-to-silo pings",
    [
      ["sent", M("Microsoft.Orleans.orleans_messaging_pings_sent")],
      ["received", M("Microsoft.Orleans.orleans_messaging_pings_received")],
      ["replies received", M("Microsoft.Orleans.orleans_messaging_pings_reply_received")],
    ],
    { w: 3, description: "Sent without a matching reply is a silo that has stopped answering" },
  ),
  series(
    "Dispatcher",
    [
      ["received", M("Microsoft.Orleans.orleans_messaging_processing_dispatcher_received"), "max"],
      ["processed", M("Microsoft.Orleans.orleans_messaging_processing_dispatcher_processed"), "max"],
      ["forwarded", M("Microsoft.Orleans.orleans_messaging_processing_dispatcher_forwarded"), "max"],
    ],
    { w: 3, description: "Cumulative totals read every 30 s, so read the slope rather than the value" },
  ),
  series(
    "Directory cache",
    [
      ["lookups issued", M("Microsoft.Orleans.orleans_directory_lookups_cache_issued")],
      ["cache hits", M("Microsoft.Orleans.orleans_directory_lookups_cache_successes")],
    ],
    { w: 3, description: "The gap between the two is what had to go ask another silo" },
  ),
  series(
    "Directory size",
    [
      ["cache entries", M("Microsoft.Orleans.orleans_directory_cache_size"), "avg"],
      ["partition entries", M("Microsoft.Orleans.orleans_directory_partition_size"), "avg"],
      ["ring size", M("Microsoft.Orleans.orleans_directory_ring_size"), "avg"],
    ],
    { w: 3 },
  ),
  series(
    "Directory registrations",
    [
      ["registrations", M("Microsoft.Orleans.orleans_directory_registrations")],
      ["p95 duration (ms)", M("Microsoft.Orleans.orleans_directory_registration_duration"), "p95"],
      ["snapshot transfers", M("Microsoft.Orleans.orleans_directory_snapshot_transfer_count")],
    ],
    { w: 3 },
  ),
  series(
    "Ring balance (%)",
    [
      ["this silo", M("Microsoft.Orleans.orleans_consistent_ring_range_percentage_local"), "avg"],
      ["cluster average", M("Microsoft.Orleans.orleans_consistent_ring_range_percentage_average"), "avg"],
      ["directory local portion", M("Microsoft.Orleans.orleans_directory_ring_local_portion_percentage"), "avg"],
    ],
    { w: 3, description: "A silo far from the average owns a disproportionate slice of the ring" },
  ),

  series(
    "Gateway",
    [
      ["sent", M("Microsoft.Orleans.orleans_gateway_sent")],
      ["received", M("Microsoft.Orleans.orleans_gateway_received")],
      ["connected clients (net)", M("Microsoft.Orleans.orleans_gateway_connected_clients")],
    ],
    { w: 3 },
  ),
  series(
    "Grain storage p95 (ms)",
    [
      ["read", M("Microsoft.Orleans.orleans_storage_read_latency"), "p95"],
      ["write", M("Microsoft.Orleans.orleans_storage_write_latency"), "p95"],
    ],
    { w: 3, description: "Redis-backed grain state" },
  ),
  line("Grain state read p95 by store (ms)", [q(M("Microsoft.Orleans.orleans_storage_read_latency"), ["p95"], { columns: ["state_name"] })], {
    w: 3,
    description: "channel-store, account-deletion-store, user-level-store … — which grain's state is expensive to load",
  }),
  series(
    "Reminders",
    [
      ["active", M("Microsoft.Orleans.orleans_reminders_active"), "avg"],
      ["ticks delivered", M("Microsoft.Orleans.orleans_reminders_ticks_delivered")],
      ["tardiness p95", M("Microsoft.Orleans.orleans_reminders_tardiness"), "p95"],
    ],
    { w: 3, description: "Tardiness is how late a tick was; it climbing is a scheduler that cannot keep up" },
  ),
  series(
    "Silo memory (bytes)",
    [
      ["available", M("Microsoft.Orleans.orleans_runtime_available_memory"), "avg"],
      ["total physical", M("Microsoft.Orleans.orleans_runtime_total_physical_memory"), "avg"],
    ],
    { w: 3 },
  ),
  series(
    "Cluster health",
    [
      ["watchdog checks", M("Microsoft.Orleans.orleans_watchdog_health_checks")],
      ["sockets opened", M("Microsoft.Orleans.orleans_networking_sockets_opened")],
      ["sockets closed", M("Microsoft.Orleans.orleans_networking_sockets_closed")],
      ["messages rerouted", M("Microsoft.Orleans.orleans_messaging_rerouted")],
      ["connected gateways", M("Microsoft.Orleans.orleans_client_connected_gateways"), "avg"],
    ],
    { w: 3 },
  ),
  series(
    "Rebalancing",
    [
      ["checks", M("Argon.argon_orleans_rebalance_checks")],
      ["accepted", M("Argon.argon_orleans_rebalance_accepted")],
      ["rejected (cooldown)", M("Argon.argon_orleans_rebalance_rejected", "reason:cooldown")],
      ["p95 imbalance (activations)", M("Argon.argon_orleans_imbalance_value"), "p95"],
    ],
    { w: 3, description: "ArgonImbalanceToleranceRule. Imbalance is the spread of activations across silos it measured" },
  ),
];

/** The process itself: GC, the thread pool, and what this server calls out to. */
const RUNTIME: WidgetDraft[] = [
  big("Exceptions", M("System.Runtime.dotnet.exceptions")),
  big("Working set (MB)", M("System.Runtime.dotnet.process.memory.working_set"), (f) => `${f("avg")} / 1048576`),
  big("Heap (MB)", M("System.Runtime.dotnet.gc.last_collection.heap.size"), (f) => `${f("avg")} / 1048576`),
  big("Threads (max)", M("System.Runtime.dotnet.thread_pool.thread.count"), "max"),
  big("Queue length (max)", M("System.Runtime.dotnet.thread_pool.queue.length"), "max",
    "Work items waiting for a thread. Pods run with a 1-core quota, so this backs up early"),
  big("CPU count", M("System.Runtime.dotnet.process.cpu.count"), "max", "What the runtime believes it has"),

  series(
    "Managed memory (bytes)",
    [
      ["heap after last GC", M("System.Runtime.dotnet.gc.last_collection.heap.size"), "avg"],
      ["committed", M("System.Runtime.dotnet.gc.last_collection.memory.committed_size"), "avg"],
      ["fragmentation", M("System.Runtime.dotnet.gc.last_collection.heap.fragmentation.size"), "avg"],
      ["working set", M("System.Runtime.dotnet.process.memory.working_set"), "avg"],
    ],
    { w: 3 },
  ),
  by("GC collections by generation", M("System.Runtime.dotnet.gc.collections"), "gc.heap.generation", "max", {
    w: 3,
    description: "Cumulative per generation: read the slope. gen2 climbing steadily is the one to worry about",
  }),
  series(
    "Thread pool",
    [
      ["threads", M("System.Runtime.dotnet.thread_pool.thread.count"), "avg"],
      ["queue length", M("System.Runtime.dotnet.thread_pool.queue.length"), "max"],
      ["timers", M("System.Runtime.dotnet.timer.count"), "avg"],
    ],
    { w: 3, description: "Every pod has a CPU limit of one core, so the pool starts at one thread" },
  ),
  series(
    "Contention and exceptions",
    [
      ["lock contentions", M("System.Runtime.dotnet.monitor.lock_contentions"), "max"],
      ["exceptions thrown", M("System.Runtime.dotnet.exceptions")],
    ],
    { w: 3 },
  ),
  by("CPU time by mode (s)", M("System.Runtime.dotnet.process.cpu.time"), "cpu.mode", "max", {
    w: 3,
    description: "Cumulative user/system CPU seconds; the slope is the utilisation",
  }),
  series(
    "GC pause and allocation",
    [
      ["pause time (s)", M("System.Runtime.dotnet.gc.pause.time"), "max"],
      ["total allocated (bytes)", M("System.Runtime.dotnet.gc.heap.total_allocated"), "max"],
    ],
    { w: 3, description: "Both cumulative; a steepening pause-time slope is GC eating the CPU quota" },
  ),
  series(
    "JIT",
    [
      ["compiled methods", M("System.Runtime.dotnet.jit.compiled_methods"), "max"],
      ["compilation time (s)", M("System.Runtime.dotnet.jit.compilation.time"), "max"],
      ["assemblies loaded", M("System.Runtime.dotnet.assembly.count"), "max"],
    ],
    { w: 3, description: "Flat after warm-up; a restart shows as a reset to zero" },
  ),

  big("Outbound calls", M(HTTP_OUT), "count"),
  big("Outbound p50 (s)", M(HTTP_OUT), "p50"),
  big("Outbound p95 (s)", M(HTTP_OUT), "p95"),
  line("Outbound p95 by host (s)", [q(M(HTTP_OUT), ["p95"], { columns: ["server.address"] })], {
    w: 3,
    description: "S3, Vault, Xsolla, Aegis and Sentry itself — whichever of them is slow today",
  }),
  bars("Outbound calls by host", M(HTTP_OUT), "server.address", "count", { w: 3 }),
  by("Outbound calls by status", M(HTTP_OUT), STATUS, "count", { w: 3 }),
  line("Outbound connections by host", [q(M("System.Net.Http.http.client.connection.duration"), ["count"], { columns: ["server.address"] })], {
    w: 3,
    description: "One measurement per connection closed: a host churning connections is not being kept alive",
  }),
  series(
    "Outbound connection pool",
    [
      ["connection lifetime p95 (s)", M("System.Net.Http.http.client.connection.duration"), "p95"],
      ["time queued p95 (s)", M("System.Net.Http.http.client.request.time_in_queue"), "p95"],
    ],
    { w: 3, description: "Time in queue is a request waiting for a free connection: the pool is too small" },
  ),
  series(
    "Kestrel memory pool",
    [
      ["rented", M("Microsoft.AspNetCore.MemoryPool.aspnetcore.memory_pool.rented")],
      ["returned to pool", M("Microsoft.AspNetCore.MemoryPool.aspnetcore.memory_pool.pooled")],
      ["newly allocated", M("Microsoft.AspNetCore.MemoryPool.aspnetcore.memory_pool.allocated")],
      ["evicted", M("Microsoft.AspNetCore.MemoryPool.aspnetcore.memory_pool.evicted")],
    ],
    { w: 3, description: "Allocated tracking rented means the pool is not recycling buffers" },
  ),
];

/** People: who is online, how they get in, and what they do once they are. */
const PRODUCT: WidgetDraft[] = [
  big("Online (avg)", M("Argon.argon_user_online_count"), "avg"),
  big("Online (peak)", M("Argon.argon_user_online_count"), "max"),
  big("Sessions started", M("Argon.argon_user_sessions_started")),
  big("Registrations", M("Argon.argon_user_registrations")),
  big("Messages sent", M("Argon.argon_channel_messages_sent")),
  big("Voice joins", M("Argon.argon_channel_voice_joins")),

  series(
    "Online users",
    [
      ["average", M("Argon.argon_user_online_count"), "avg"],
      ["peak", M("Argon.argon_user_online_count"), "max"],
    ],
    { w: 3, description: "Counted from Redis presence keys every 15 s, so pod deaths cannot inflate it" },
  ),
  series(
    "Sessions",
    [
      ["started", M("Argon.argon_user_sessions_started")],
      ["ended", M("Argon.argon_user_sessions_ended")],
      ["expired", M("Argon.argon_user_session_expirations")],
      ["active per silo", M("Argon.argon_user_sessions_active"), "avg"],
    ],
    { w: 3, description: "Expired without a matching end is a client that vanished rather than logged out" },
  ),
  percentiles("Session duration (s)", M("Argon.argon_user_session_duration"), { w: 3 }),
  by("Session heartbeats by status", M("Argon.argon_user_session_heartbeats"), "status", "sum", {
    w: 3,
    description: "The client's liveness signal, split by the status it reported. A drop with sessions still open means the heartbeat path is broken",
  }),
  by("Status changes", M("Argon.argon_user_status_changes"), "to_status", "sum", {
    w: 3,
    description: "Where people moved to: online / away / do-not-disturb / offline",
  }),

  series(
    "Authorization",
    [
      ["attempts", M("Argon.argon_authorization_attempts")],
      ["external (OAuth)", M("Argon.argon_external_authorization_attempts")],
      ["OTP sent", M("Argon.argon_authorization_otp_sent")],
      ["password resets", M("Argon.argon_password_resets")],
    ],
    { w: 3 },
  ),
  percentiles("Authorization latency (ms)", M("Argon.argon_authorization_duration"), { w: 3 }),
  series(
    "Registration",
    [
      ["registrations", M("Argon.argon_user_registrations")],
      ["p95 duration (ms)", M("Argon.argon_user_registration_duration"), "p95"],
    ],
    { w: 3 },
  ),

  series(
    "Messages",
    [
      ["sent", M("Argon.argon_channel_messages_sent")],
      ["typing events", M("Argon.argon_channel_typing_events")],
    ],
    { w: 3 },
  ),
  percentiles("Message send latency (ms)", M("Argon.argon_channel_message_send_duration"), { w: 3 }),
  series(
    "High-water mark coalescing",
    [
      ["messages sent", M("Argon.argon_channel_messages_sent")],
      ["durable writes", M("Argon.argon_channel_last_message_flushes")],
      ["writes avoided", M("Argon.argon_channel_last_message_absorbed")],
      ["failed writes", M("Argon.argon_channel_last_message_flushes", "result:failed")],
    ],
    {
      w: 3,
      description: "Writes tracking messages means the flush timer is not firing. A failed write only retries while the activation lives",
    },
  ),
  series(
    "Reactions and moderation actions",
    [
      ["reactions added", M("Argon.argon_channel_reactions_added")],
      ["reactions removed", M("Argon.argon_channel_reactions_removed")],
      ["member kicks", M("Argon.argon_channel_member_kicks")],
    ],
    { w: 3 },
  ),

  series(
    "Voice",
    [
      ["joins", M("Argon.argon_channel_voice_joins")],
      ["leaves", M("Argon.argon_channel_voice_leaves")],
      ["recordings started", M("Argon.argon_channel_recordings_started")],
      ["recordings stopped", M("Argon.argon_channel_recordings_stopped")],
    ],
    { w: 3 },
  ),
  percentiles("Voice session duration (s)", M("Argon.argon_channel_voice_session_duration"), { w: 3 }),

  series(
    "Account deletion",
    [
      ["requested", M("Argon.argon_deletion_requested")],
      ["scheduled", M("Argon.argon_deletion_scheduled")],
      ["completed", M("Argon.argon_deletion_completed")],
      ["cancelled", M("Argon.argon_deletion_cancelled")],
      ["rejected", M("Argon.argon_deletion_rejected")],
      ["failed", M("Argon.argon_deletion_failed")],
    ],
    { w: 3, description: "Rejected carries a `reason`; failed means the execution phase threw after the grace period" },
  ),
  by("Deletions by trigger", M("Argon.argon_deletion_requested"), "trigger", "sum", {
    w: 3,
    description: "auto_inactivity is the operator-approved dormant-account queue; operator is a person pressing the button",
  }),
  series(
    "Deletion execution",
    [
      ["p95 duration (s)", M("Argon.argon_deletion_execution_duration"), "p95"],
      ["reminders sent", M("Argon.argon_deletion_reminders_sent")],
    ],
    { w: 3 },
  ),
  series(
    "Data export",
    [
      ["requested", M("Argon.argon_export_requested")],
      ["started", M("Argon.argon_export_started")],
      ["completed", M("Argon.argon_export_completed")],
      ["failed", M("Argon.argon_export_failed")],
      ["cancelled", M("Argon.argon_export_cancelled")],
      ["rate limited", M("Argon.argon_export_rate_limited")],
    ],
    { w: 3 },
  ),
  series(
    "Export archives",
    [
      ["p95 duration (s)", M("Argon.argon_export_duration"), "p95"],
      ["p95 tick (ms)", M("Argon.argon_export_tick_duration"), "p95"],
      ["p95 archive (bytes)", M("Argon.argon_export_archive_size_bytes"), "p95"],
    ],
    { w: 3 },
  ),
];

/** What the server runs on: Redis, object storage, the moderation model, the TTL sweeper. */
const PLATFORM: WidgetDraft[] = [
  big("Redis ops", M("Argon.argon_redis_operations")),
  big("Redis p95 (ms)", M("Argon.argon_redis_operation_duration"), "p95"),
  big("Cache ops", M("Argon.argon_redis_distributed_cache_operations")),
  big("Cache p95 (ms)", M("Argon.argon_redis_distributed_cache_operation_duration"), "p95"),
  big("Uploads finalized", M("Argon.argon_storage_uploads_finalized")),
  big("Moderation rejections", M("Argon.argon_moderation_rejections_total")),

  by("Redis operations", M("Argon.argon_redis_operations"), "operation", "sum", { w: 3 }),
  by("Redis outcomes", M("Argon.argon_redis_operations"), "result", "sum", {
    w: 3,
    description: "A failure line here is the cache path degrading before anything else notices",
  }),
  percentiles("Redis latency (ms)", M("Argon.argon_redis_operation_duration"), { w: 3 }),
  line("Redis p95 by operation (ms)", [q(M("Argon.argon_redis_operation_duration"), ["p95"], { columns: ["operation"] })], { w: 3 }),
  series(
    "Redis pool",
    [
      ["taken", M("Argon.argon_redis_connections_taken"), "avg"],
      ["allocated", M("Argon.argon_redis_connections_total"), "avg"],
      ["max size", M("Argon.argon_redis_pool_max_size"), "avg"],
    ],
    { w: 3, description: "Taken pressed against max size is a pool about to make callers wait" },
  ),
  line("Pool occupancy by profile", [q(M("Argon.argon_redis_connections_taken"), ["avg"], { columns: ["profile"] })], {
    w: 3,
    description: "Cache / HybridCache / OrleansStorage each get their own pool",
  }),
  series(
    "Redis pool churn",
    [
      ["rented", M("Argon.argon_redis_connections_rented")],
      ["returned", M("Argon.argon_redis_connections_returned")],
      ["returned faulted", M("Argon.argon_redis_connections_returned_faulted")],
      ["allocated", M("Argon.argon_redis_connections_allocated")],
      ["scale-ups", M("Argon.argon_redis_pool_scale_ups")],
      ["removed by cleanup", M("Argon.argon_redis_pool_connections_removed")],
    ],
    { w: 3 },
  ),
  by("Distributed cache operations", M("Argon.argon_redis_distributed_cache_operations"), "operation", "sum", { w: 3 }),
  series(
    "Cache and retries",
    [
      ["cache p95 (ms)", M("Argon.argon_redis_distributed_cache_operation_duration"), "p95"],
      ["operation retries", M("Argon.argon_redis_operation_retries")],
      ["key expirations seen", M("Argon.argon_redis_key_expiration_events")],
    ],
    { w: 3, description: "Retries are replica-write, READONLY and LOADING errors being ridden out" },
  ),

  series(
    "Uploads",
    [
      ["requested", M("Argon.argon_storage_uploads_requested")],
      ["finalized", M("Argon.argon_storage_uploads_finalized")],
      ["failed", M("Argon.argon_storage_uploads_failed")],
    ],
    { w: 3, description: "Requested minus finalized is what clients started and abandoned" },
  ),
  series(
    "Upload size and finalize (p95)",
    [
      ["size (bytes)", M("Argon.argon_storage_upload_size_bytes"), "p95"],
      ["finalize (ms)", M("Argon.argon_storage_upload_finalize_duration"), "p95"],
    ],
    { w: 3 },
  ),
  by("S3 operations", M("Argon.argon_storage_s3_operations"), "operation", "sum", { w: 3, description: "head / delete / put" }),
  line("S3 p95 by operation (ms)", [q(M("Argon.argon_storage_s3_operation_duration"), ["p95"], { columns: ["operation"] })], { w: 3 }),
  series(
    "Blob references",
    [
      ["increments", M("Argon.argon_storage_ref_increments")],
      ["decrements", M("Argon.argon_storage_ref_decrements")],
      ["presigned GETs", M("Argon.argon_storage_presigned_get_generated")],
      ["public URLs served", M("Argon.argon_storage_public_urls_served")],
    ],
    { w: 3 },
  ),
  series(
    "Storage GC",
    [
      ["expired blobs swept", M("Argon.argon_storage_gc_blobs_swept")],
      ["orphans swept", M("Argon.argon_storage_gc_orphans_swept")],
      ["errors", M("Argon.argon_storage_gc_errors")],
      ["p95 sweep (ms)", M("Argon.argon_storage_gc_sweep_duration"), "p95"],
    ],
    { w: 3 },
  ),

  series(
    "Moderation",
    [
      ["evaluations", M("Argon.argon_moderation_evaluations_total")],
      ["rejections", M("Argon.argon_moderation_rejections_total")],
      ["skipped (no model)", M("Argon.argon_moderation_evaluations_skipped")],
      ["violations recorded", M("Argon.argon_moderation_violations_recorded")],
    ],
    { w: 3, description: "Skipped climbing means the ONNX model is unavailable and content is going through unchecked" },
  ),
  series(
    "Moderation latency p95 (ms)",
    [
      ["end to end", M("Argon.argon_moderation_evaluation_duration"), "p95"],
      ["S3 download", M("Argon.argon_moderation_s3_download_duration"), "p95"],
      ["inference", M("Argon.argon_moderation_inference_duration"), "p95"],
    ],
    { w: 3 },
  ),

  by("TTL sweep passes", M("Argon.argon_ttl_sweep_passes"), "outcome", "sum", {
    w: 3,
    description: "Alert on Failed. A backlog existing is normal in report mode; a backlog growing across passes is not",
  }),
  series(
    "TTL sweeper",
    [
      ["rows deleted", M("Argon.argon_ttl_sweep_rows")],
      ["backlog", M("Argon.argon_ttl_sweep_backlog"), "max"],
      ["p95 pass (ms)", M("Argon.argon_ttl_sweep_duration"), "p95"],
    ],
    { w: 3 },
  ),
  by("Cross-region grain calls", M("Argon.argon_region_foreign_calls"), "outcome", "sum", {
    w: 3,
    description: "routed = sent to the region that owns the id; anything else is an id nobody can serve",
  }),
];

/** Everything with a third party on the other end: bots, billing, phone verification. */
const INTEGRATIONS: WidgetDraft[] = [
  big("Bot events", M("Argon.argon_bot_events_published")),
  big("SSE deliveries", M("Argon.argon_bot_sse_events_delivered")),
  big("Commands", M("Argon.argon_bot_command_invocations")),
  big("Payments", M("Argon.argon_xsolla_payments_processed")),
  big("Webhooks", M("Argon.argon_xsolla_webhooks_received")),
  big("Codes sent", M("Argon.argon_phone_verification_sent")),

  series(
    "Bot event publishing",
    [
      ["published", M("Argon.argon_bot_events_published")],
      ["publish errors", M("Argon.argon_bot_event_publish_errors")],
      ["p95 publish (ms)", M("Argon.argon_bot_event_publish_duration"), "p95"],
    ],
    { w: 3, description: "Events onto NATS for the bot gateway" },
  ),
  by("Bot events by type", M("Argon.argon_bot_events_published"), "event_type", "sum", { w: 3 }),
  series(
    "Bot SSE",
    [
      ["opened", M("Argon.argon_bot_sse_connections_opened")],
      ["closed", M("Argon.argon_bot_sse_connections_closed")],
      ["active", M("Argon.argon_bot_sse_connections_active"), "avg"],
      ["events delivered", M("Argon.argon_bot_sse_events_delivered")],
    ],
    { w: 3, description: "Opened without a matching close is a leaked stream holding a thread" },
  ),
  series(
    "Slash commands",
    [
      ["invocations", M("Argon.argon_bot_command_invocations")],
      ["errors", M("Argon.argon_bot_command_errors")],
      ["p95 dispatch (ms)", M("Argon.argon_bot_command_dispatch_duration"), "p95"],
    ],
    { w: 3 },
  ),

  series(
    "Xsolla webhooks",
    [
      ["received", M("Argon.argon_xsolla_webhooks_received")],
      ["errors", M("Argon.argon_xsolla_webhook_errors")],
      ["signature failures", M("Argon.argon_xsolla_webhook_signature_failures")],
      ["p95 handling (ms)", M("Argon.argon_xsolla_webhook_duration"), "p95"],
    ],
    { w: 3, description: "Signature failures are either a rotated secret or someone knocking" },
  ),
  by("Webhooks by type", M("Argon.argon_xsolla_webhooks_received"), "type", "sum", { w: 3 }),
  series(
    "Money",
    [
      ["payments", M("Argon.argon_xsolla_payments_processed")],
      ["refunds", M("Argon.argon_xsolla_refunds_processed")],
      ["checkouts created", M("Argon.argon_xsolla_checkouts_created")],
    ],
    { w: 3, description: "Checkouts created against payments processed is the drop-off through the payment page" },
  ),
  by("Revenue by currency", M("Argon.argon_xsolla_payment_revenue"), "currency", "sum", {
    w: 3,
    description: "Summed in the currency charged — do not add the series together",
  }),
  series(
    "Subscriptions and boosts",
    [
      ["created", M("Argon.argon_xsolla_subscriptions_created")],
      ["canceled", M("Argon.argon_xsolla_subscriptions_canceled")],
      ["boosts granted", M("Argon.argon_xsolla_boosts_granted")],
    ],
    { w: 3 },
  ),
  series(
    "Xsolla API",
    [
      ["errors", M("Argon.argon_xsolla_api_call_errors")],
      ["p50 (ms)", M("Argon.argon_xsolla_api_call_duration"), "p50"],
      ["p95 (ms)", M("Argon.argon_xsolla_api_call_duration"), "p95"],
    ],
    { w: 3 },
  ),

  series(
    "Phone verification",
    [
      ["codes sent", M("Argon.argon_phone_verification_sent")],
      ["checks", M("Argon.argon_phone_verification_checks")],
      ["fallbacks", M("Argon.argon_phone_verification_fallbacks")],
      ["Telegram ability checks", M("Argon.argon_phone_telegram_send_ability_checks")],
    ],
    { w: 3, description: "A fallback is Telegram declining and the SMS provider taking over — each one costs money" },
  ),
  series(
    "Phone provider latency p95 (ms)",
    [
      ["send", M("Argon.argon_phone_verification_send_duration"), "p95"],
      ["check", M("Argon.argon_phone_verification_check_duration"), "p95"],
    ],
    { w: 3 },
  ),
  series(
    "Phone spend",
    [
      ["verification cost", M("Argon.argon_phone_verification_cost")],
      ["Telegram balance (reported)", M("Argon.argon_phone_telegram_balance")],
    ],
    {
      w: 3,
      description: "Balance is declared as a synchronous Gauge<decimal>, which the Sentry bridge classifies as a counter — read it as the sum of reported readings, not as a level",
    },
  ),
];

const DASHBOARDS: Dashboard[] = (
  [
    ["Argon · Server · API", API],
    ["Argon · Server · Orleans", ORLEANS],
    ["Argon · Server · Runtime", RUNTIME],
    ["Argon · Server · Product", PRODUCT],
    ["Argon · Server · Platform", PLATFORM],
    ["Argon · Server · Integrations", INTEGRATIONS],
  ] as const
).map(([title, widgets]) => {
  // The dashboards API rejects 30 or more widgets per dashboard.
  if (widgets.length >= 30) {
    throw new Error(`"${title}": ${widgets.length} widgets, Sentry allows fewer than 30 per dashboard`);
  }
  return {
    title,
    widgets: layout([...widgets]),
    projects: [PROJECT],
    period: "7d",
    environment: [],
    filters: {},
  };
});

const selected = () =>
  onlyFilter ? DASHBOARDS.filter((d) => d.title.toLowerCase().includes(onlyFilter.toLowerCase())) : DASHBOARDS;

// ───────────────────────────── API ─────────────────────────────

interface ApiResult {
  status: number;
  json: unknown;
}

async function api(token: string, method: string, path: string, body?: unknown): Promise<ApiResult> {
  const res = await fetch(`${BASE}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(API_TIMEOUT_MS),
  });
  const text = await res.text();
  let json: unknown = text;
  try {
    json = JSON.parse(text);
  } catch {
    /* not JSON: keep the text */
  }
  return { status: res.status, json };
}

/** Runs every widget through the server-side validator. Cheap and non-destructive. */
async function validate(token: string): Promise<boolean> {
  let ok = true;
  for (const dashboard of selected()) {
    for (const { layout: _layout, ...widget } of dashboard.widgets) {
      const startedAt = performance.now();
      const res = await api(token, "POST", `/organizations/${ORG}/dashboards/widgets/`, widget);
      console.log(
        `  ${res.status < 300 ? "ok " : "ERR"} ${String(Math.round(performance.now() - startedAt)).padStart(6)} ms  ${widget.title}`,
      );
      if (res.status >= 300) {
        ok = false;
        console.error(`INVALID "${widget.title}": HTTP ${res.status} ${JSON.stringify(res.json).slice(0, 500)}`);
      }
    }
    console.log(`${dashboard.title}: ${dashboard.widgets.length} widgets validated${ok ? "" : " (with errors)"}`);
  }
  return ok;
}

/**
 * Asks the events API for every widget's first query over the dashboard period and reports whether
 * anything came back. A widget that validates can still be permanently empty — a wrong unit returns
 * null rather than an error, and a group-by on an attribute the metric does not carry returns
 * nothing at all. This is how that is caught before someone stares at an empty chart.
 */
async function probe(token: string): Promise<void> {
  for (const dashboard of selected()) {
    console.log(`\n${dashboard.title}`);
    for (const widget of dashboard.widgets) {
      const verdicts: string[] = [];
      for (const query of widget.queries) {
        const params = new URLSearchParams([
          ["dataset", "tracemetrics"],
          ["project", String(PROJECT)],
          ["statsPeriod", dashboard.period],
          ["referrer", "api.explore.metrics-table"],
          ["per_page", "5"],
        ]);
        if (query.conditions) params.append("query", query.conditions);
        for (const field of query.fields) params.append("field", field);

        const res = await api(token, "GET", `/organizations/${ORG}/events/?${params}`);
        const rows = (res.json as { data?: Array<Record<string, unknown>> })?.data ?? [];
        const aggregate = query.aggregates[0]!;
        const values = rows.map((r) => r[aggregate]).filter((v) => v !== null && v !== undefined);
        verdicts.push(
          res.status >= 300
            ? `HTTP ${res.status}`
            : values.length === 0
              ? "empty"
              : `${values.length} row(s)`,
        );
      }
      const empty = verdicts.every((v) => v === "empty");
      console.log(`  ${empty ? "EMPTY" : "data "}  ${widget.title}  [${verdicts.join(", ")}]`);
    }
  }
}

async function create(token: string): Promise<void> {
  const existing = await api(token, "GET", `/organizations/${ORG}/dashboards/`);
  if (existing.status !== 200 || !Array.isArray(existing.json)) {
    throw new Error(`listing dashboards failed: HTTP ${existing.status} ${JSON.stringify(existing.json).slice(0, 300)}`);
  }
  const current = existing.json as Array<{ id: string; title: string }>;

  for (const dashboard of selected()) {
    for (const old of current.filter((x) => x.title === dashboard.title)) {
      const del = await api(token, "DELETE", `/organizations/${ORG}/dashboards/${old.id}/`);
      console.log(`replaced old "${old.title}" (${old.id}): HTTP ${del.status}`);
    }

    const res = await api(token, "POST", `/organizations/${ORG}/dashboards/`, dashboard);
    if (res.status >= 300) {
      console.error(`FAILED "${dashboard.title}": HTTP ${res.status}\n${JSON.stringify(res.json, null, 2).slice(0, 3000)}`);
      process.exitCode = 1;
      continue;
    }
    const id = (res.json as { id: string }).id;
    console.log(
      `created "${dashboard.title}": ${HOST}/organizations/${ORG}/dashboard/${id}/  (${dashboard.widgets.length} widgets)`,
    );
  }
}

// ───────────────────────────── main ─────────────────────────────

/**
 * The token, from the environment only — never prompted for, the way the other settings are: a
 * prompt echoes what is typed, and a shell keeps a history.
 */
function requireToken(): string {
  const token = process.env.SENTRY_AUTH_TOKEN?.trim();
  if (!token) {
    console.error("SENTRY_AUTH_TOKEN is not set (needs org:read, org:write, project:read)");
    process.exit(2);
  }
  return token;
}

if (has("--dry")) {
  for (const d of selected()) console.log(`${d.title}: ${d.widgets.length} widgets`);
  const sample = DASHBOARDS[0]!.widgets.find((w) => w.title === "Requests by status code");
  console.log(JSON.stringify(sample, null, 2));
} else if (has("--dump")) {
  // Every dashboard as the JSON the API receives; handy for replaying one widget with curl.
  console.log(JSON.stringify(selected()));
} else if (has("--validate")) {
  const ok = await validate(requireToken());
  process.exit(ok ? 0 : 1);
} else if (has("--probe")) {
  await probe(requireToken());
} else if (has("--create")) {
  await create(requireToken());
} else {
  console.log("usage: bun scipts/sentry/dashboards.ts --dry | --dump | --validate | --probe | --create [--only <title part>]");
  process.exit(2);
}

// Top-level await needs a module.
export {};
