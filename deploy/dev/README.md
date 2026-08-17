# Local development configuration

Everything a checkout needs to run the server on one machine, with no compose file and no cluster.
Nothing here is a secret in any real sense: the keys are generated for this folder and are committed
on purpose, so a fresh clone starts without a setup step.

```
argon-server --role dev
```

One process hosting every role. `--role dev` is the `single-instance` topology; the ten-role split is
`distributed`, and `--roles` lists both.

## How it is wired in

Two mechanisms, each carrying what it is for:

| | |
|---|---|
| `conf.d/<feature>.json` | one file per feature, holding only that feature's own sections |
| `dev.json` | the settings no feature owns — connection strings, Orleans cluster ids, the ticket key |

Every launch profile points `ARGON_CONFIG_DIR` and `ARGON_CONFIG_FILE` at them. Running it by hand is
the same two variables — absolute, because `dotnet run --project` runs from the project directory
rather than the one you typed the command in:

```powershell
$env:ARGON_CONFIG_DIR  = "$PWD/deploy/dev/conf.d"
$env:ARGON_CONFIG_FILE = "$PWD/deploy/dev/dev.json"
dotnet run --project src/Argon.Api -- --role dev --topology single-instance
```

A path that does not resolve is reported by name rather than ignored, so a wrong one says so.

Precedence is `appsettings.json` → `conf.d/*.json` → `dev.json` → environment variables, so a one-off
override never needs a file edited:

```powershell
$env:Kestrel__Argon__Port = "5010"
```

Check the whole thing without starting anything:

```
argon-server --validate-config --role dev
```

## What has to be running

| service | where | required? |
|---|---|---|
| PostgreSQL | `localhost:5432`, `postgres`/`postgres`, database `argon` | **yes** — created and migrated on first start |
| Redis-compatible | `localhost:6379` | **yes** — five logical databases, one per profile |
| NATS | `localhost:4222` | **yes** — see below |
| SeaweedFS S3 | `localhost:9321`, `argon`/`argon` | no — uploads only |
| LiveKit | `localhost:7880` | no — voice only |

NATS is required in a way worth knowing about: without it the process reaches *Application started*,
and then `HybridPermissionCacheAdapter` fails to subscribe and the host stops a few seconds later. It
looks like a healthy start followed by an unexplained exit. It is not — it is a missing event bus, and
the first line of the `BackgroundService failed` block says so.

## What it listens on

| port | address | |
|---|---|---|
| 5002 | all | HTTP and the first-party Ion surface |
| 8920 | all | admin console |
| 8930 | all | developer account console |
| 11111 | the machine's own IP | Orleans silo |
| 30000 | the machine's own IP | Orleans client gateway |

The two Orleans ports bind the primary interface rather than every address — that is what
`ConfigureEndpoints` advertises to the rest of the cluster, and it is normal.

Dragonfly, SeaweedFS, NATS and LiveKit are each a single binary; none of them needs a container.
`Database:Provider` is `PostgreSql` here rather than the production `CockroachDb`, which suppresses
the Cockroach-only DDL (`LOCALITY`, row-level TTL) the migrations would otherwise emit.

`Argon:Cluster:Id` is `argon-dev` — a cluster of its own, so a dev silo never joins the membership
rows the integration suite leaves behind under the default id.

## Not configured

`OperatorAuth` and `AccountConsoleAuth` validate tokens against the Aegis OAuth provider, which has
no local equivalent. Both sections are absent, and both interceptors refuse every call when they find
them absent — so the admin console on `:8920` and the developer console on `:8930` listen but answer
nothing. That is the safe direction, and pointing them at the live provider from a laptop would not be.
