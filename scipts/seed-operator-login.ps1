# Local operator login for the admin console (control app), without a hardware key.
#
# The admin console on :8920 only accepts a token Aegis stamps `typ=operator`, and Aegis only stamps
# it when a Redis "verified" mark exists for the signing-in user. That mark is normally written by
# touching a YubiKey over mutual TLS. On a local stand there is no proxy, no Vault CA and no cert —
# so this writes the same mark by hand, which is the only thing the key ever produced.
#
# Nothing here is a code change and nothing here works anywhere but a local stand: the mark is a
# Redis key on localhost, and the token still has to be minted by the local Aegis and validated
# against its local JWKS. See deploy/dev/conf.d/operator-auth.json for the JWKS config the api role reads — named for the
# feature, because a conf.d file whose basename is not a feature name is skipped with a warning.
#
# Re-run it before every fresh sign-in: the mark is single-use — Aegis deletes it the moment a token
# is issued, so a full re-login needs a fresh one. A refresh token keeps the session alive in between.

param(
    [string]$Postgres = "argon-postgres",
    [string]$Redis    = "argon-redis",
    [string]$Database = "argon",
    [string]$User     = "postgres",

    # titsex, the account this operator is attached to. `SELECT "Id" FROM "Users"` if it differs.
    [string]$UserId   = "01a0ab93-483f-7001-ba3f-2e7df983d258",
    [string]$Email    = "aravan962@gmail.com",
    [string]$Name     = "titsex",

    # The control app's OAuth client id (useOAuth.ts) and where its dev server redirects back.
    [string]$ClientId = "3D50AD7F7BAFED67C81DBB1CE22B1F2D",
    [string]$Redirect = "http://localhost:5006/callback",

    # Set the rows only, or the Redis mark only. Neither switch does both when one is given.
    [switch]$RowsOnly,
    [switch]$MarkOnly
)

$ErrorActionPreference = "Stop"

$OperatorId = "aaaaaaaa-0000-4000-8000-0000000000ad"
$AppId      = "aaaaaaaa-0000-4000-8000-0000000000c0"

# ── The rows: an operator attached to the account, and the control app as an internal OAuth app ────
#
# The operator row is what an operator token's system_operator role comes from (IsSystemOperator).
#
# The app is registered NOT internal, on purpose. An internal app makes the login gate demand team
# membership *and* an @argon.gl staff mailbox (AppsManagementGrain.CanBeLoginForAppAsync) — which a
# gmail dev account can never pass. A public, verified, non-internal app lets any account sign in,
# and the operator token is still minted: Aegis stamps the operator claims from the Redis mark at
# token time regardless of the internal flag (AuthController.Authorize). The flag only gates the
# step-up preflight, which the mark already satisfies.
#
# The team is taken from the web client's own row rather than hardcoded, so this works whatever id
# that team was seeded with.

if (-not $MarkOnly) {
    $sql = @"
DO `$`$
DECLARE
    team uuid;
BEGIN
    SELECT "TeamId" INTO team FROM "DevApps" WHERE "ClientId" = 'A37E7A1DB06E9610C9C0BD77C61A821B';
    IF team IS NULL THEN
        RAISE EXCEPTION 'No web-client app row to borrow a team from — register the web client first (see argon-local-oauth-app-row).';
    END IF;

    INSERT INTO "DevApps" (
        "AppId", "TeamId", "Name", "Description", "ClientId", "ClientSecret", "VerificationKey",
        "AppType", "RequiredScopes", "AllowedRedirects", "IsInternalApp", "AllowMagicLink",
        "CreatedAt", "UpdatedAt", "IsDeleted")
    VALUES (
        '$AppId', team, 'Argon Control (local)', 'Local admin console', '$ClientId', 'local-not-a-secret', NULL,
        2, ARRAY['identity','offline_access','user.read'], ARRAY['$Redirect'], false, false,
        now(), now(), false)
    ON CONFLICT ("AppId") DO UPDATE SET
        "ClientId"         = EXCLUDED."ClientId",
        "RequiredScopes"   = EXCLUDED."RequiredScopes",
        "AllowedRedirects" = EXCLUDED."AllowedRedirects",
        "IsInternalApp"    = false,
        "IsDeleted"        = false,
        "UpdatedAt"        = now();

    INSERT INTO "ClientApps" ("AppId", "Platform", "RateLimitPerMinute", "IsVerified", "IsPublic", "WebsiteUrl", "RepositoryUrl")
    VALUES ('$AppId', 3, 120, true, true, NULL, NULL)
    ON CONFLICT ("AppId") DO UPDATE SET "IsPublic" = true, "IsVerified" = true;

    INSERT INTO "Operators" (
        "Id", "DisplayName", "Email", "UserId", "IsActive", "IsSystemOperator",
        "CreatedAt", "UpdatedAt", "IsDeleted")
    VALUES (
        '$OperatorId', '$Name', '$Email', '$UserId', true, true, now(), now(), false)
    ON CONFLICT ("Id") DO UPDATE SET
        "UserId"           = EXCLUDED."UserId",
        "IsActive"         = true,
        "IsSystemOperator" = true,
        "IsDeleted"        = false,
        "UpdatedAt"        = now();
END
`$`$;

SELECT o."DisplayName", o."IsSystemOperator", a."IsInternalApp"
FROM "Operators" o, "DevApps" a
WHERE o."Id" = '$OperatorId' AND a."AppId" = '$AppId';
"@

    Write-Host "Seeding operator + app rows..."
    $sql | docker exec -i $Postgres psql -U $User -d $Database
}

# ── The mark: what the hardware key would have written ─────────────────────────────────────────────
#
# Redis DB 0, key aegis:operator-verified:{userId}, a JSON OperatorVerificationState. Every field the
# authorize flow copies onto the token has to be here — the interceptor dereferences operator_id,
# operator_email and operator_cert_thumbprint without a null check, so a missing one is a 500, not a
# clean refusal. Ten minutes, the same window the real step-up gets, and single-use.

if (-not $RowsOnly) {
    $state = '{"OperatorId":"' + $OperatorId + '","OperatorEmail":"' + $Email + '","CertThumbprint":"DEVNOCERT","DisplayName":"' + $Name + '","IsSystemOperator":true}'
    $key   = "aegis:operator-verified:$UserId"

    Write-Host "Writing the verification mark (Redis db0, 10 min)..."
    docker exec -i $Redis redis-cli -n 0 SET $key $state EX 600 | Out-Null
    docker exec -i $Redis redis-cli -n 0 TTL $key

    Write-Host ""
    Write-Host "Signed-off. Open http://localhost:5006 and sign in as $Name within 10 minutes."
    Write-Host "The mark is spent when the token is issued — re-run this (or with -MarkOnly) before the next full sign-in."
}
