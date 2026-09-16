# Argon Server

Backend server for [Argon](https://argon.gl) — voice communication platform.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://www.docker.com/products/docker-desktop)
- [mkcert](https://github.com/FiloSottile/mkcert) — only for local voice over TLS, see `deploy/dev/README.md`

## Quick Start

```bash
# Clone with the public submodules (Ion contracts, bot API docs)
git clone --recurse-submodules https://github.com/argon-chat/server.git
cd server

# Start dependencies: PostgreSQL, Redis, NATS, SeaweedFS, LiveKit
docker compose -f deploy/docker-compose.yml up -d

# Build & run — one process hosting every role
dotnet build
dotnet run --project src/Argon.Api -- --role dev --topology single-instance
```

The compose file and `deploy/dev/` are two halves of one thing: the first starts the services, the
second is the configuration that points at them. [deploy/dev/README.md](deploy/dev/README.md) covers
what is required, what is optional, and what is deliberately not configured on a laptop.

## Submodules

| Path | What | Who can clone it |
| --- | --- | --- |
| `src/Argon.Ion` | Ion contracts of the client-facing API | everyone |
| `src/Argon.IonAdmin` | Ion contracts of the admin console | everyone |
| `docs/bot-api-docs` | Bot API documentation site | everyone |
| `docs/internal` | Internal architecture and release notes | Argon team only (private) |

`docs/internal` is marked `update = none` in `.gitmodules`, so `git clone --recurse-submodules`
and `git submodule update --init --recursive` skip it and nothing else needs it to build or test.
CI initialises the three public submodules by name and never touches it.

Argon team members opt in once per clone (the `none` setting is copied into `.git/config` by
`git submodule init`, so it has to be overridden there, not only on the command line):

```bash
git config submodule.docs/internal.update checkout
git submodule update --init docs/internal
```

From then on `git submodule update --recursive` keeps it in step with the others.

## Project Structure

```
src/
├── Argon.Api/      # API & grains
├── Argon.Core/     # Core library
└── Argon.CodeGen/  # Code generation
tests/
└── ArgonComplexTest/
deploy/             # Docker configs
```

## License

[Business Source License 1.1](LICENSE.md)

- Free for internal and non-production use
- Cannot be offered as a competing hosted service
- Converts to MPL 2.0 after 4 years

Licensor: Argon Inc. LLC  
Contact: privacy@argon.gl
