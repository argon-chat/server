# Argon Server

**Argon** is a high-performance, distributed voice chat server built on modern cloud-native technologies. It provides real-time communication capabilities with a focus on scalability, reliability, and low latency.

## 🌟 Features

- **Real-time Voice Communication**: Built on LiveKit for high-quality, low-latency voice chat
- **Distributed Architecture**: Leverages Orleans virtual actors for scalable, distributed computing
- **Multi-Region Support**: CockroachDB cluster with multi-region replication
- **Event-Driven**: NATS messaging with JetStream for reliable event streaming
- **S3-Compatible Storage**: SeaweedFS for scalable object storage
- **High-Performance Caching**: DragonflyDB for Redis-compatible in-memory operations

---

## 🛠️ Technology Stack

| Component | Technology |
|-----------|-----------|
| **Framework** | .NET 10.0 (Preview) |
| **Language** | C# (Latest) |
| **Actor Model** | Microsoft Orleans |
| **Database** | CockroachDB (3-node cluster) |
| **NoSQL** | ScyllaDB (Cassandra-compatible) |
| **Cache** | DragonflyDB (Redis-compatible) |
| **Messaging** | NATS with JetStream |
| **Storage** | SeaweedFS (S3-compatible) |
| **Media Server** | LiveKit |
| **Reverse Proxy** | Caddy |

---

## 📋 Prerequisites

Before getting started, ensure you have:

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Preview)
- [Docker Desktop](https://www.docker.com/products/docker-desktop) (or Docker Engine + Compose)
- [mkcert](https://github.com/FiloSottile/mkcert) — for generating local TLS certificates
- PowerShell (for Windows) or Bash (for Linux/macOS) for running setup scripts

---

## 🚀 Quick Start

### 1. Clone the Repository

```bash
git clone https://github.com/argon-chat/server.git
cd server
```

### 2. Set Up Local Infrastructure

Navigate to the deploy directory and start the local development environment:

```bash
cd deploy
./ensure-certs.ps1  # Generate local TLS certificates
docker compose -f docker-compose.local.yml up -d
```

This will start all required services (databases, caching, messaging, media server, etc.).

### 3. Build the Server

From the repository root:

```bash
dotnet restore
dotnet build
```

### 4. Run the Server

```bash
cd src/Argon.Api
dotnet run
```

---

## 📦 Local Development Environment

This section explains the complete local infrastructure for development using Docker Compose.

### Infrastructure Components

The local environment includes the following services:

| Service | Description |
|----------|--------------|
| **NATS** | Messaging bus with JetStream enabled for event streaming |
| **ScyllaDB** | High-performance Cassandra-compatible NoSQL database |
| **CockroachDB (3 nodes)** | Distributed SQL database cluster with multi-region support |
| **SeaweedFS (S3)** | Local S3-compatible object storage |
| **KineticaFS** | Argon's file lifecycle manager and S3 bridge |
| **DragonflyDB** | High-performance Redis-compatible in-memory cache |
| **LiveKit** | Real-time media server for voice/video communication |
| **Caddy** | HTTPS reverse proxy for LiveKit |

All services are connected through the **`argon`** Docker bridge network.

---

### 🔐 Generating Local Certificates

Before starting the containers, generate local certificates used by **Caddy** and **LiveKit**.

**Run:**

```powershell
cd deploy
./ensure-certs.ps1
```

This script will:
- Check for existing certificates in the `certs/` directory  
- Create new certificates via `mkcert` if they don't exist

---

### 🚀 Starting the Infrastructure

> The `docker-compose.local.yml` file is located in the `/deploy` directory.

To launch the local infrastructure, run:

```bash
cd deploy
docker compose -f docker-compose.local.yml up -d
```

This will start all services in detached mode (`-d`).

> The first startup may take a few minutes as the databases initialize.

**Alternative**: You can also use the provided PowerShell script:

```powershell
cd deploy
./start.ps1
```

---

### 🧠 Service URLs and Ports

| Service | URL/Port | Notes |
|----------|-----|-------|
| **NATS** | `localhost:4222` | NATS client port |
| **NATS Web UI** | [http://localhost:8222](http://localhost:8222) | Server monitoring |
| **ScyllaDB CQL** | `localhost:9042` | CQL port |
| **CockroachDB SQL** | `localhost:26257` | SQL port |
| **CockroachDB UI** | [http://localhost:8080](http://localhost:8080) | Admin console |
| **SeaweedFS S3** | [http://localhost:8333](http://localhost:8333) | S3 API endpoint |
| **SeaweedFS UI** | [http://localhost:9321](http://localhost:9321) | Admin interface |
| **KineticaFS** | [http://localhost:3000](http://localhost:3000) | File lifecycle manager |
| **DragonflyDB** | `localhost:6379` | Redis-compatible port |
| **LiveKit** | `localhost:7880` | LiveKit server port |
| **LiveKit (HTTPS)** | [https://localhost:9443](https://localhost:9443) | Served via Caddy proxy |

---

### 🧹 Stopping and Cleaning Up

To stop all containers:

```bash
cd deploy
docker compose -f docker-compose.local.yml down
```

**Alternative**: Use the PowerShell script:

```powershell
cd deploy
./stop.ps1
```

To stop all containers and remove related volumes (resetting all data):

```bash
cd deploy
docker compose -f docker-compose.local.yml -p argonlocal down --volumes --remove-orphans
```

This will:
- Stop and remove all running containers  
- Remove persistent volumes  
- Clean up orphaned resources

---

### 🗂 Directory Structure

```
.
├── src/                      # Source code
│   ├── Argon.Api/           # Main API server
│   ├── Argon.Core/          # Core business logic
│   ├── Argon.Cassandra/     # ScyllaDB/Cassandra integration
│   ├── Argon.CodeGen/       # Code generation utilities
│   └── Argon.Ion/           # Ion protocol implementation
├── tests/                    # Test projects
├── deploy/                   # Deployment configurations
│   ├── certs/               # Local TLS certificates (generated)
│   ├── docker/              # Docker-related scripts/configs
│   ├── dynamicconfig/       # Temporal dynamic configuration
│   ├── docker-compose.local.yml  # Main Docker Compose file
│   ├── ensure-certs.ps1     # Certificate generation script
│   ├── start.ps1            # Start infrastructure script
│   ├── stop.ps1             # Stop infrastructure script
│   ├── Caddyfile            # Caddy reverse proxy configuration
│   └── livekit.yaml         # LiveKit server configuration
├── Argon.Server.slnx        # Solution file
└── README.md                # This file
```

---

### 🧩 Notes

- The **CockroachDB cluster** automatically bootstraps with 3 nodes (`cockroach1`, `cockroach2`, `cockroach3`) in different regions.
- **KineticaFS** waits for ScyllaDB to become healthy before starting.
- **DragonflyDB** provides a fast Redis-compatible cache and stores data in the `argon-cache-data` volume.
- **Caddy** uses certificates from `./certs` to serve LiveKit over HTTPS on port **9443**.

---

### 🧰 Troubleshooting

| Issue | Possible Fix |
|--------|--------------|
| `mkcert` not found | Install it from [mkcert GitHub](https://github.com/FiloSottile/mkcert) |
| Database healthcheck failures | Wait 1–2 minutes; some databases (Scylla/CockroachDB) have long startup times |
| Ports already in use | Check for conflicting local services and stop them |
| SSL errors in browser | Run `mkcert -install` to add the local CA to your OS trust store |
| CockroachDB cluster init fails | Run `docker compose logs init` to see initialization logs |

---

## 🏗️ Building and Running the Server

### Build

To build the entire solution:

```bash
dotnet restore
dotnet build
```

### Run

To run the Argon API server:

```bash
cd src/Argon.Api
dotnet run
```

The server will start and connect to the local infrastructure services.

### Configuration

The server configuration is managed through:
- `appsettings.json` - Application settings
- Environment variables - Runtime configuration
- User secrets - Development secrets (use `dotnet user-secrets`)

---

## 🧪 Testing

To run tests:

```bash
dotnet test
```

---

## 🤝 Contributing

Contributions are welcome! Please follow these guidelines:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Development Guidelines

- Follow the existing code style and conventions
- Write tests for new features
- Update documentation as needed
- Ensure all tests pass before submitting PR

---

## 📄 License

This project is licensed under the **Business Source License 1.1**.

- **Licensor**: Argon Inc. LLC
- **Change Date**: 4 years from publication
- **Change License**: MPL 2.0

See [LICENSE.md](LICENSE.md) for full details.

### Key Points:

- ✅ Free for internal use within organizations
- ✅ Free for non-production use
- ✅ Free for modifications and derivative works
- ⚠️ Cannot offer as a competing hosted service
- ⏰ Automatically becomes MPL 2.0 after 4 years

For alternative licensing arrangements, contact: privacy@argon.gl

---

## 📞 Support and Contact

- **Repository**: [https://github.com/argon-chat/server](https://github.com/argon-chat/server)
- **Issues**: [GitHub Issues](https://github.com/argon-chat/server/issues)
- **Email**: privacy@argon.gl

---

## 🙏 Acknowledgments

Built with ❤️ by the Argon team and contributors.

Special thanks to all the open-source projects that make Argon possible:
- Microsoft Orleans
- CockroachDB
- ScyllaDB
- LiveKit
- NATS
- And many more...

---

✅ **Ready to build!**  
You now have everything you need to start developing with Argon Server.
