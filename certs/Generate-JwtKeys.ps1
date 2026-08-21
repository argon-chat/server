Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Get-Command "openssl" -ErrorAction SilentlyContinue)) {
    Write-Error "❌ OpenSSL not found"
    exit 1
}

$target = Join-Path $PSScriptRoot "jwt-keys"
New-Item -ItemType Directory -Force -Path $target | Out-Null

Write-Host "🔐 Generating keys..."
Write-Host "Saving to: $target`n"

function Run-OpenSSL {
    param([Parameter(Mandatory)] [string[]] $Args)

    $p = Start-Process -FilePath "openssl" -ArgumentList $Args `
        -WorkingDirectory $target -NoNewWindow -Wait -PassThru `
        -RedirectStandardError "$target\_last_error.txt" `
        -RedirectStandardOutput "$target\_last_output.txt"

    if ($p.ExitCode -ne 0) {
        $err = Get-Content "$target\_last_error.txt" -Raw
        throw "OpenSSL error ($($Args -join ' ')): $err"
    }
}

function To-B64([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    return [Convert]::ToBase64String($bytes)
}

# --- 1️⃣ ECDSA RAW (PEM) ---
Run-OpenSSL @("ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", "ec-private.pem")
Run-OpenSSL @("ec", "-in", "ec-private.pem", "-outform", "DER", "-out", "ec-private.der")
Run-OpenSSL @("ec", "-in", "ec-private.pem", "-pubout", "-outform", "DER", "-out", "ec-public.der")

$ecPrivB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $target "ec-private.der")))
$ecPubB64  = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $target "ec-public.der")))

# --- 2️⃣ RSA RAW (PEM) ---
Write-Host "→ RSA (2048)"
Run-OpenSSL @("genrsa", "-out", "rsa-private.pem", "2048")
Run-OpenSSL @("rsa", "-in", "rsa-private.pem", "-pubout", "-out", "rsa-public.pem")

$rsaPrivPath = Join-Path $target "rsa-private.pem"
$rsaPubPath  = Join-Path $target "rsa-public.pem"
$rsaPrivB64  = To-B64 $rsaPrivPath
$rsaPubB64   = To-B64 $rsaPubPath

# --- 3️⃣ X509 ECDSA ---
Write-Host "→ X.509 ECDSA"
Run-OpenSSL @("req", "-x509", "-new", "-nodes", "-key", "ec-private.pem", "-sha256", "-days", "365", "-subj", "/CN=EC-Example", "-out", "ec-cert.pem")
Run-OpenSSL @("pkcs12", "-export", "-out", "ec-cert.pfx", "-inkey", "ec-private.pem", "-in", "ec-cert.pem", "-password", "pass:1234")

$ecX509Path = Join-Path $target "ec-cert.pfx"
$ecX509b64  = To-B64 $ecX509Path

# --- 4️⃣ X509 RSA ---
Write-Host "→ X.509 RSA"
Run-OpenSSL @("req", "-x509", "-new", "-nodes", "-key", "rsa-private.pem", "-sha256", "-days", "365", "-subj", "/CN=RSA-Example", "-out", "rsa-cert.pem")
Run-OpenSSL @("pkcs12", "-export", "-out", "rsa-cert.pfx", "-inkey", "rsa-private.pem", "-in", "rsa-cert.pem", "-password", "pass:1234")

$rsaX509Path = Join-Path $target "rsa-cert.pfx"
$rsaX509b64  = To-B64 $rsaX509Path

# --- Вывод ---
Write-Host ""
Write-Host "✅ Base64 outputs (ready for JwtOptions):"
Write-Host "------------------------------------------------------"

Write-Host "`n# 🔹 ECDSA PrivateKey (PEM → Base64):"
Write-Host $ecPrivB64
Write-Host "`n# 🔹 ECDSA PublicKey (PEM → Base64):"
Write-Host $ecPubB64

Write-Host "`n# 🔸 RSA PrivateKey (PEM → Base64):"
Write-Host $rsaPrivB64
Write-Host "`n# 🔸 RSA PublicKey (PEM → Base64):"
Write-Host $rsaPubB64

Write-Host "`n# 🧩 X509 ECDSA (PFX → Base64, password: 1234):"
Write-Host $ecX509b64
Write-Host "`n# 🧩 X509 RSA (PFX → Base64, password: 1234):"
Write-Host $rsaX509b64

Write-Host "`nAll files saved in: $target"
