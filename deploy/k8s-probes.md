# Kubernetes probes, silo drain and client stop

What a pod needs so a deployment moves work instead of dropping it. Both kinds of role need it, for
different reasons.

A **silo** holds activations, and a deployment has to hand them to the rest of the cluster before the
process ends. A **client role** — `entrypoint`, `aegis`, `botapi`, `account`, `admin` — holds no
activations, and that was once read as needing nothing: removing the pod from the Service is the
whole of its drain, and Kubernetes does that on its own. The second half is true and the first half
missed what a client role actually holds. `entrypoint` terminates every client websocket and attaches
an `IUserSessionGrain` to each connection, and Kubernetes removes a pod from a Service *after* its
readiness fails — so with no readiness probe there was nothing to fail, nothing to notice, and the
pod left the Service only once it had already stopped. Every live socket was severed by a pod that
was still being handed new ones.

Both kinds answer the same four endpoints, at the same paths, filtered by the same tags. What differs
is what stands behind the answers.

## The sequence, silo

1. Pod starts. `startupProbe` fails until the silo reaches `Active` in the membership table, which
   holds `livenessProbe` off — a slow join is not a wedged process.
2. `readinessProbe` passes. Kubernetes adds the pod to the service; the silo takes grains.
3. Deployment begins. Kubernetes calls `preStop`.
4. `preStop` drains: the silo marks itself draining, `readinessProbe` starts failing, Kubernetes takes
   it out of the service, and the silo hands its activations to the rest of the cluster.
5. `preStop` returns only once the drain is done. Kubernetes then sends `SIGTERM` and Orleans shuts
   down gracefully, moving through `Stopping` to `Dead` in the membership table.

`livenessProbe` stays healthy through all of it. Its only remedy is a restart, and restarting a pod
mid-handover destroys what the drain was protecting.

## The sequence, client role

1. Pod starts. Kestrel is listening before the Orleans client has connected — the web host is built
   first — so the endpoints answer during a window in which the process cannot serve anything.
   `startupProbe` fails through it.
2. The cluster client reaches a gateway. `startupProbe` passes and never fails again;
   `readinessProbe` passes. Kubernetes adds the pod to the service and clients connect.
3. Deployment begins. Kubernetes calls `preStop`.
4. `preStop` reports the process not-ready, then waits — `HostHooks:PreStopLeadTime`, twenty seconds
   by default. During the wait the pod is fully able to serve and is deliberately saying it is not,
   so Kubernetes fails the readiness probe, updates the EndpointSlice, and reprograms kube-proxy and
   whatever fronts it. Nothing tells a pod when that has landed, which is why this is a clock rather
   than a wait on a signal.
5. `preStop` returns. Kubernetes sends `SIGTERM`, the host stops, and the websockets still attached
   close. Their clients reconnect and land on a pod that is in the service and ready.

`livenessProbe` never fails on a client role. The cluster client retries forever by design, so a
restart cannot reconnect it any sooner than it is already reconnecting — it can only cost the pod
every socket it is holding. The probe that *can* end a client pod is `startupProbe`, and the line it
draws is "never connected" rather than "not connected right now": a pod that has never reached the
cluster is misconfigured and worth restarting, and one that reached it and lost it is in an outage it
shares with everything else.

### What this does not do

Readiness going false stops **new** connections being routed to the pod. It does nothing to the
sockets already open: nothing inside the hub consults it, so a connection that is already attached
stays attached until the process stops. That is enough for a rolling deployment — the sockets move
when the process ends, and they move to a pod that is ready — and it is short of what a maintenance
window wants, which is existing sockets migrating on their own before anything stops. Admission
gating inside the hub, and a server-initiated "reconnect elsewhere", are still unbuilt.

The lead time is therefore the whole of the guarantee: it buys the pod's removal from the Service,
not the emptying of the pod.

## Manifest, silo

```yaml
spec:
  # Longer than a drain takes, or the kill arrives mid-handover. The drain itself gives up after
  # five minutes, so this is that plus room for the graceful stop.
  terminationGracePeriodSeconds: 420

  containers:
    - name: silo
      lifecycle:
        preStop:
          exec:
            # Blocks until the drain finishes, which is what buys the drain its time.
            command: ["sh", "-c", "curl -sS --max-time 400 http://127.0.0.1:8080/internal/shutdown"]

      startupProbe:
        httpGet: { path: /health/startup, port: 8080 }
        periodSeconds: 5
        # Five minutes to join before the pod is given up on.
        failureThreshold: 60

      readinessProbe:
        httpGet: { path: /health/ready, port: 8080 }
        periodSeconds: 5
        # One failure is enough: a draining silo should leave the service immediately.
        failureThreshold: 1

      livenessProbe:
        httpGet: { path: /health/live, port: 8080 }
        periodSeconds: 10
        failureThreshold: 3
```

Silo roles do not configure a port, so it is the host default.

## Manifest, client role

```yaml
spec:
  # The pre-stop wait, plus the host's own shutdown timeout for requests in flight, plus room. Too
  # short and the kill lands during the wait, which puts the pod back to leaving the service after
  # it has already stopped.
  terminationGracePeriodSeconds: 90

  containers:
    - name: entrypoint
      lifecycle:
        preStop:
          exec:
            # Blocks for HostHooks:PreStopLeadTime. --max-time only has to outlast that.
            command: ["sh", "-c", "curl -sS --max-time 60 http://127.0.0.1:5002/internal/shutdown"]

      startupProbe:
        httpGet: { path: /health/startup, port: 5002 }
        periodSeconds: 5
        # A minute to reach a gateway. Past that the pod is talking to the wrong cluster or to
        # nothing, and a restart is worth trying.
        failureThreshold: 12

      readinessProbe:
        httpGet: { path: /health/ready, port: 5002 }
        periodSeconds: 5
        # One failure is enough, and the pre-stop lead time is sized against this period.
        failureThreshold: 1

      livenessProbe:
        httpGet: { path: /health/live, port: 5002 }
        periodSeconds: 10
        failureThreshold: 3
```

The port is whatever the role's Kestrel listens on — `Kestrel:Argon:Port`, which client roles do set.
`5002` above is the shipped development value; use the deployment's.

**Check the scheme before copying this.** A client role binds exactly one listener, and it is HTTPS
whenever the role has a certificate configured — `Kestrel:Argon:UseFileCertificate` or
`UseLocalhostCertificate`. Production configures one, so on production that port speaks TLS and
nothing else: the plain-HTTP probes above would fail every check and the `curl` in `preStop` would
fail with the pod already on its way out. Where a certificate is configured, the probes need

```yaml
        httpGet: { path: /health/ready, port: 5002, scheme: HTTPS }
```

and the hook needs `curl -sk https://127.0.0.1:5002/internal/shutdown` — `-k` because the certificate
is issued for the public name, not for the loopback address the hook dials. A deployment that
terminates TLS at a proxy and leaves both certificate switches off keeps the plain-HTTP form.

Client roles need `HostHooks:PreStopHook` left on, which is the default. With it off there is no
`/internal/shutdown` to call and the pod is stopped by signal, which is the behaviour that severed
the sockets.

## The endpoints

Same paths on both kinds of role, and the probes filter on the tags `startup`, `liveness` and
`readiness` rather than on the check names.

| path | on a silo | on a client role |
|---|---|---|
| `/health/startup` | has the silo joined the cluster — fails while its status is not `Active` — and can it reach what it depends on (below) | has the cluster client ever reached a gateway — fails until it has, passes for good after — and can it reach what it depends on |
| `/health/ready` | should this silo be given work — fails while draining, or while its status is not `Active`; a dependency it cannot reach is reported as `Degraded`, which is still `200` | should this pod be given connections — fails once a stop is requested, and only then; dependencies reported the same way |
| `/health/live` | is this process worth keeping — fails when the silo status is `Dead`, meaning the cluster wrote it off while the process kept running | never fails; a restart cannot help a client that is already retrying, and it costs the pod its sockets |
| `/health` | everything above, the dependency checks and the configuration verdict, with detail | for humans and dashboards, not for probes |

A client role's readiness deliberately does not look at the gateway count. Only `core` exposes a
cluster gateway, so gating on it would take entrypoint, aegis, botapi, account and admin out of their
Services at the same instant on any `core` blip — including everything they serve that never touches
Orleans. The count is reported on `/health`, where an operator can see it, and is not acted on by
removing the pod.

The three probe paths answer with the status code and one word. `/health` is the only one that
returns the detailed report, and only to a caller on the loopback address: on a client role that
listener is the public one, and the detail names every check with its full data — this region's
gateway count, whether the pod is mid-shutdown, and any exception a failing check produced. A
non-local caller gets what a probe gets. Scrape it from a sidecar or from `kubectl exec`, not through
the Service.

`/internal/shutdown` accepts loopback only. The one thing it does is end the process, and nothing
outside the pod has business asking for that — which is also why `preStop` uses `exec` with `curl`
rather than `httpGet`, since Kubernetes sends `httpGet` probes from the node rather than from inside
the pod.

`/internal/undrain` returns a silo to service after a cancelled maintenance window. It answers 404 on
a client role, and that is the honest answer: a client role's stop is a countdown to exit rather than
a state it sits in, so there is nothing to cancel. Cancelling a client rollout means stopping the
rollout, not un-stopping the pod.

## What the probes check beyond the process

A process can join the cluster, take its place in the Service and still be unable to serve: the
database connection string names the old cluster, the NATS URL is the other region's, a
NetworkPolicy keeps it from Redis. Under a rolling update that surfaces as errors on the pods that
were just promoted; under a blue/green deployment it surfaces as the new colour taking all the
traffic and failing every request, because nothing the probes looked at was wrong.

So every feature that opens a connection to something outside the process registers a check for
it, and a role probes exactly what it uses:

| check | registered by | what it does |
|---|---|---|
| `database` | `database` | a pooled connection and `SELECT 1` over it, bypassing EF's retry |
| `nats` | every role | connect, ping, and JetStream account info — a server without JetStream accepts the connection and refuses every stream |
| `redis` | `cache` | a ping on every profile that has a connection string, on that profile's logical database |
| `object-storage` | `file-storage` | a list of at most one key in the bucket, and in the export bucket where one is set |
| `vault` | `vault`, only where a Vault is configured | `sys/health`: initialised and unsealed |
| `sfu` | `sfu` | a room lookup by a name no room has — proves the URL, key and secret without walking the rooms |

Which probes those checks may fail is the `Probes` section, owned by the `probes` feature every
role carries. The shipped defaults:

```json
{
  "Probes": {
    "Dependencies": {
      "Startup": "Fail",
      "Readiness": "Degrade",
      "Liveness": "Off",
      "Timeout": "00:00:05",
      "Overrides": {}
    }
  }
}
```

Each probe is set separately because Kubernetes does something different with each, and the
defaults follow from what that costs:

- **startup: `Fail`.** It is asked until it passes once, and a pod it never passes on is one that is
  never routed to and never promoted. That is the deployment gate: a pod that cannot reach what it
  needs stays out, the old colour keeps serving, and the rollout is turned back or held for someone
  to look at. `failureThreshold × periodSeconds` on the startup probe is how long a colour gets to
  reach its dependencies before the pod is restarted.
- **readiness: `Degrade`.** Failing readiness takes a pod out of its Service, and every pod of every
  role shares the same database and the same Redis. Failing all of them on a shared outage turns a
  partial failure into a total one — the same reasoning that keeps a client role's readiness from
  following the gateway count. The failure is reported as `Degraded`, which the endpoint shows and
  Kubernetes reads as `200`. A deployment that would rather remove such pods sets `Fail`.
- **liveness: `Off`.** Its only remedy is a restart, and a restart does not bring a database back; it
  only costs the pod the connections it was holding.

`Overrides` gates one dependency differently from the rest, keyed by the check's name. Vault is the
usual candidate, since only operator step-up reads it:

```json
"Overrides": { "vault": { "Startup": "Degrade" } }
```

Every check is bounded by `Timeout`, applied on top of whatever the client's own connect timeout is
— NATS is configured to wait a minute, which is a minute after Kubernetes stopped listening.

## When the configuration is wrong

The same rules run in two places and end the same way. The validator sidecar runs
`--validate-config --role <name>` against the mounted `conf.d` and exits non-zero on any error,
which is where a pipeline stops the rollout before a pod of the new colour exists. A role started
anyway refuses to boot on the same findings, so its pod never passes the startup probe and the
rollout never promotes it.

A role that does start keeps the validator's warnings: `/health` lists them under the
`configuration` check, healthy, so the first question about a pod behaving oddly — is it running
the configuration you think — is one `curl` away.

## What has to be true for this to work

- **More than one silo.** A drain with nowhere to send activations skips the migration and falls back
  to waiting; the calls end when the pod does. Roll one pod at a time and keep at least two.
- **More than one replica of each client role**, for the same shape of reason: readiness going false
  only helps if there is somewhere else in the Service for the reconnects to land.
- **`maxUnavailable: 1`** on the rolling update, for both.
- **The new pod is ready before the old one drains.** `maxSurge: 1` with readiness gating gives that.
- **`terminationGracePeriodSeconds` exceeds the pre-stop block.** A drain that gives up after five
  minutes, or a client lead time of twenty seconds plus the host's shutdown timeout — whichever the
  role does, the grace period has to outlast it.
- **A blue/green rollout promotes on the new colour being ready, not on it being scheduled.** The
  startup probe is where a pod that cannot reach its dependencies, or was given a configuration it
  cannot start on, is held; a controller that switches the Service on pod creation switches it onto
  exactly those pods. Argo Rollouts and Flagger both wait for availability; a hand-rolled selector
  switch has to do the same, and treat a startup probe that has not passed within its
  `failureThreshold` as the signal to roll back.
