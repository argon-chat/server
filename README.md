# Argon Server

Backend server for [Argon](https://argon.gl) — voice communication platform.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://www.docker.com/products/docker-desktop)
- [mkcert](https://github.com/FiloSottile/mkcert)

## Quick Start

```bash
# Clone
git clone https://github.com/argon-chat/server.git
cd server

# Start dependencies
cd deploy
./ensure-certs.ps1
docker compose -f docker-compose.local.yml up -d

# Build & run
cd ..
dotnet build
cd src/Argon.Api
dotnet run
```

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
