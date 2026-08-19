# Kubernetes probes and blue-green drain

What a silo pod needs so a deployment moves calls instead of dropping them. Client roles need none of
this: they hold no activations, so removing them from the service endpoints is the whole of their
drain and Kubernetes does that on its own.

## The sequence

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

## Manifest

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

The port is whatever the role's Kestrel listens on; silo roles do not configure it, so it is the
host default.

## The endpoints

| path | answers | fails when |
|---|---|---|
| `/health/startup` | has the silo joined the cluster | silo status is not `Active` |
| `/health/ready` | should this silo be given work | draining, or silo status is not `Active` |
| `/health/live` | is this process worth keeping | silo status is `Dead` — the cluster wrote it off while the process kept running |
| `/health` | everything above, with detail | for humans and dashboards, not for probes |

`/internal/shutdown` accepts loopback only. The one thing it does is end the process, and nothing
outside the pod has business asking for that — which is also why `preStop` uses `exec` with `curl`
rather than `httpGet`, since Kubernetes sends `httpGet` probes from the node rather than from inside
the pod.

## What has to be true for this to work

- **More than one silo.** A drain with nowhere to send activations skips the migration and falls back
  to waiting; the calls end when the pod does. Roll one pod at a time and keep at least two.
- **`maxUnavailable: 1`** on the rolling update, for the same reason.
- **The new pod is ready before the old one drains.** `maxSurge: 1` with readiness gating gives that.
