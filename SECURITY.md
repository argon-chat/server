# Security Guide

## Overview

This document outlines security best practices for deploying Argon Server in production environments.

## Configuration Security

### Development vs Production

- **`appsettings.json`**: Contains development-only configuration with placeholder secrets. **NEVER use these values in production!**
- **`appsettings.Production.json.example`**: Template for production configuration. Copy this file and replace all `${VARIABLE}` placeholders with actual values.

### Secret Management

For production deployments, use one of these approaches:

#### Option 1: Environment Variables (Recommended for simple deployments)

ASP.NET Core automatically reads configuration from environment variables using the pattern `Section__SubSection__Key`.

Example:
```bash
export ConnectionStrings__Default="Host=prod-db;Port=5432;..."
export TicketJwt__Key="your-secure-random-key-here"
export Jwt__MachineSalt="another-secure-random-key"
```

#### Option 2: HashiCorp Vault (Recommended for enterprise deployments)

The application includes built-in support for HashiCorp Vault. Configure via environment variables:

```bash
export VAULT_ADDR="https://vault.yourdomain.com"
export VAULT_TOKEN="your-vault-token"
```

Or use AppRole authentication:
```bash
export VAULT_ADDR="https://vault.yourdomain.com"
export VAULT_ROLE_ID="your-role-id"
export VAULT_SECRET_ID="your-secret-id"
```

#### Option 3: Docker Secrets / Kubernetes Secrets

For containerized deployments, mount secrets as files or environment variables:

```yaml
# docker-compose.yml example
services:
  argon-api:
    environment:
      - ConnectionStrings__Default=/run/secrets/db_connection
    secrets:
      - db_connection

secrets:
  db_connection:
    external: true
```

## Generating Secure Secrets

### Random Keys and Salts

Use cryptographically secure random generators:

```bash
# Generate a 64-character hex key (256 bits)
openssl rand -hex 32

# Or using Python
python3 -c "import secrets; print(secrets.token_hex(32))"
```

### JWT Certificate Keys

Generate new ECDSA key pairs for production:

```bash
# Generate private key
openssl ecparam -name prime256v1 -genkey -noout -out jwt-private.pem

# Generate public key
openssl ec -in jwt-private.pem -pubout -out jwt-public.pem

# Convert to base64 for configuration
cat jwt-private.pem | base64 -w 0
cat jwt-public.pem | base64 -w 0
```

## Security Checklist for Production

- [ ] Replace ALL secrets in appsettings.json with production values
- [ ] Use environment variables or Vault for secret management
- [ ] Generate new, cryptographically secure random keys
- [ ] Use HTTPS/TLS for all external connections
- [ ] Enable database connection encryption
- [ ] Configure proper CORS policies
- [ ] Set up rate limiting
- [ ] Enable audit logging
- [ ] Regularly rotate secrets and credentials
- [ ] Keep dependencies up to date
- [ ] Run security scans (CodeQL, OWASP, etc.)
- [ ] Configure proper firewall rules
- [ ] Use strong passwords for all services (Redis, NATS, databases)
- [ ] Disable development/debug features

## Secret Rotation

Regularly rotate the following secrets:
- JWT signing keys (coordinate with clients)
- Database credentials (use Vault rotation if available)
- API tokens for external services
- Transport exchange keys
- TOTP secrets

## Vulnerability Reporting

If you discover a security vulnerability, please report it to: privacy@argon.gl

Do NOT create public GitHub issues for security vulnerabilities.

## Additional Resources

- [OWASP Secure Coding Practices](https://owasp.org/www-project-secure-coding-practices-quick-reference-guide/)
- [ASP.NET Core Security Best Practices](https://learn.microsoft.com/en-us/aspnet/core/security/)
- [HashiCorp Vault Documentation](https://www.vaultproject.io/docs)
