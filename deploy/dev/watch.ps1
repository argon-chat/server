<#
.SYNOPSIS
    Runs the local Argon stand under `dotnet watch`, so editing server code restarts what it affects.

.DESCRIPTION
    Two roles, two windows, one command:

      api    https://localhost:5002   everything the client talks to
      aegis  https://localhost:5003   the sign-in widget and OAuth provider

    They are separate processes because they have to be. `HostHooksFeature` maps a version document
    at `/` on every role that does not serve a site there, and the co-hosted `dev` role deliberately
    serves no widget — so while Aegis shares a port with the API, the sign-in page is unreachable.
    That is not a development shortcut; it is the same split production runs.

    Dependencies are NOT started here — that is `deploy/docker-compose.yml`. This checks they are
    answering and names the one that is not, because the failure modes are otherwise unhelpful: a
    missing NATS lets the host reach "Application started" and then stop a few seconds later, which
    reads as a crash rather than as a missing event bus.

.EXAMPLE
    ./deploy/dev/watch.ps1 -MakeCert     # once per machine
    ./deploy/dev/watch.ps1
#>
[CmdletBinding()]
param(
    # Serve HTTPS with this certificate. Required by more than it looks: OpenIddict refuses plain
    # HTTP outright, a page served over TLS cannot call an API that is not, and `__Host-` cookies
    # are only stored on a secure origin.
    [string] $Certificate = "$PSScriptRoot/localhost.pfx",

    [string] $CertificatePassword = 'changeit',

    # Create that certificate with mkcert and exit.
    [switch] $MakeCert,

    # Skip the dependency check, for when you know Postgres is coming up behind you.
    [switch] $NoPreflight,

    [int] $ApiPort   = 5002,
    [int] $AegisPort = 5003
)

$ErrorActionPreference = 'Stop'

$repo    = (Resolve-Path "$PSScriptRoot/../..").Path
$project = Join-Path $repo 'src/Argon.Api'
$widget  = Join-Path $repo 'src/Frontend/Aegis/dist'

function Write-Step($text) { Write-Host "  $text" -ForegroundColor DarkGray }
function Write-Good($text) { Write-Host "  $text" -ForegroundColor Green }
function Write-Bad($text)  { Write-Host "  $text" -ForegroundColor Red }

# ── the certificate ──────────────────────────────────────────────────────────────────────────────

if ($MakeCert) {
    foreach ($tool in 'mkcert', 'openssl') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            Write-Bad "$tool is not on PATH"
            exit 1
        }
    }

    Push-Location $PSScriptRoot
    try {
        # mkcert writes a PEM pair; Kestrel wants a PFX. Both are kept — a vite dev server serving
        # the web client over TLS reads the PEM.
        mkcert -cert-file localhost.pem -key-file localhost-key.pem localhost 127.0.0.1 ::1
        openssl pkcs12 -export -out localhost.pfx -inkey localhost-key.pem -in localhost.pem `
            -passout "pass:$CertificatePassword"
    } finally { Pop-Location }

    Write-Good "wrote $Certificate"
    Write-Step 'run `mkcert -install` once so the browser trusts it'
    exit 0
}

if (-not (Test-Path $Certificate)) {
    Write-Bad "no certificate at $Certificate"
    Write-Host ''
    Write-Host '  ./deploy/dev/watch.ps1 -MakeCert' -ForegroundColor Yellow
    Write-Host ''
    Write-Step 'HTTPS is not optional here: OpenIddict refuses plain HTTP, and a page served over'
    Write-Step 'TLS cannot call an API that is not.'
    exit 1
}

# ── are the dependencies up ──────────────────────────────────────────────────────────────────────

function Test-Dependency([int] $port) {
    Test-NetConnection -ComputerName 127.0.0.1 -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue
}

if (-not $NoPreflight) {
    Write-Host 'checking dependencies' -ForegroundColor Cyan

    $missing = @()

    foreach ($dependency in @(
        @{ Name = 'postgres'; Port = 5432 },
        @{ Name = 'redis';    Port = 6379 },
        @{ Name = 'nats';     Port = 4222 })) {

        if (Test-Dependency $dependency.Port) { Write-Good "$($dependency.Name) on :$($dependency.Port)" }
        else { Write-Bad "$($dependency.Name) is not answering on :$($dependency.Port)"; $missing += $dependency.Name }
    }

    # The two optional ones. Uploads and voice fail without them and `/health` reports 503 the whole
    # time — which is correct, and not worth chasing when what you are working on is neither.
    if (-not (Test-Dependency 9321)) { Write-Step 'seaweedfs is down — uploads fail, /health reports 503' }
    if (-not (Test-Dependency 7880)) { Write-Step 'livekit is down — voice fails, /health reports 503' }

    if ($missing.Count -gt 0) {
        Write-Host ''
        Write-Bad "start them first: docker compose -f deploy/docker-compose.yml up -d"
        exit 1
    }

    Write-Host ''
}

# ── is a stand already up ────────────────────────────────────────────────────────────────────────

# Because the second copy does not fail in any way that explains itself. Both roles are the same
# project, so a stand that is already running holds the very artifact directories these windows build
# into — and what the developer sees is two fresh windows reporting "The build failed" with a wall of
# MSB3027 above it, naming a file lock rather than the stand they forgot was open.
$busy = @()
foreach ($listening in @(
    @{ Name = 'api';   Port = $ApiPort },
    @{ Name = 'aegis'; Port = $AegisPort })) {

    if (Test-Dependency $listening.Port) { $busy += "$($listening.Name) on :$($listening.Port)" }
}

if ($busy.Count -gt 0) {
    Write-Bad "something is already listening: $($busy -join ', ')"
    Write-Host ''
    Write-Step 'a stand is probably already running — close its windows first, or pass different'
    Write-Step 'ports with -ApiPort / -AegisPort. Starting a second copy cannot work: both build into'
    Write-Step 'the same place, and the new windows die on a file lock.'
    exit 1
}

if (-not (Test-Path $widget)) {
    Write-Step 'no built sign-in widget at src/Frontend/Aegis/dist — the OAuth page will 404'
    Write-Step 'build it once: cd src/Frontend/Aegis; bun install; bun run build'
    Write-Host ''
}

# ── the two roles ────────────────────────────────────────────────────────────────────────────────

# `dotnet watch` takes no --project and no --no-launch-profile: everything after `--` goes to the
# application. So the working directory picks the project, and the launch profile is left alone —
# it sets ASPNETCORE_ENVIRONMENT and the two ARGON_CONFIG_* paths to this very directory, which is
# what is wanted anyway, and it sets no port and no certificate, which is why the ones below win.
# Arguments after `--` do replace the profile's own, which is what lets the second window be aegis.
#
# --artifacts-path IS LOAD-BEARING. Both roles are the same project, so two watchers share one
# bin/ — and they do not take turns: every saved file wakes both, both rebuild, and one dies with
# "Argon.Core.dll is locked by Argon.Api". Not a start-up race that a sleep can paper over; it
# repeats on every edit, which is the one thing a watch setup exists to survive. Separate artifact
# directories give each its own output and the two never meet.
function Start-Role([string] $role, [int] $port, [string] $roleArgs, [hashtable] $extra) {
    $environment = @{
        # dotnet-watch injects aspnetcore-browser-refresh.js into every HTML response in Development
        # and has it open a WebSocket back to the watcher. The identity server serves the sign-in
        # widget, whose Content-Security-Policy quite correctly does not list a random localhost
        # port — so the script is blocked and the console fills with a violation that has nothing to
        # do with the page. The widget is built by vite and reloaded by vite; this refresh channel
        # was never going to reload it.
        DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH        = '1'
        Kestrel__Argon__Port                         = "$port"
        Kestrel__Argon__UseLocalhostCertificate      = 'true'
        Kestrel__Argon__LocalhostCertificatePath     = "$Certificate"
        Kestrel__Argon__LocalhostCertificatePassword = $CertificatePassword
    } + $extra

    $assignments = $environment.GetEnumerator() | ForEach-Object { "`$env:$($_.Key)='$($_.Value)'" }
    $artifacts   = Join-Path $repo ".artifacts/$role"

    $command = @(
        $assignments
        "Set-Location '$project'"
        "Write-Host 'argon $role - https://localhost:$port' -ForegroundColor Cyan"
        # --no-hot-reload, deliberately, and it is the difference between this working and only
        # seeming to. Hot reload patches method bodies into the running process and shrugs at
        # everything else — a changed option default, a DI registration, a line of middleware — with
        # "No C# changes to apply". The build succeeds, the file is saved, and the server goes on
        # running the old behaviour. For server work that is the worst possible outcome: you are
        # debugging a change that was never applied. A restart costs some seconds and is always true.
        #
        # (--non-interactive goes with it: it only means anything while hot reload is on.)
        "dotnet watch --no-hot-reload --artifacts-path '$artifacts' -- $roleArgs"
    ) -join '; '

    Start-Process pwsh -ArgumentList '-NoExit', '-Command', $command
}

Write-Host 'starting' -ForegroundColor Cyan
Write-Good "api    https://localhost:$ApiPort"
Write-Good "aegis  https://localhost:$AegisPort"
Write-Host ''
Write-Step 'edit any .cs and the role that uses it restarts on its own'
Write-Step 'the web client is separate: cd <client>; bun run dev'
Write-Host ''

Start-Role 'api' $ApiPort '--role dev --topology single-instance' @{}

# Started second because aegis is a client role: it needs a gateway to join, and the api window is
# what hosts the silo. Not fatal if it loses the race — an Orleans client retries for ever — but it
# spends the first minute logging connection refusals at anyone reading the window.
Start-Sleep -Seconds 5

Start-Role 'aegis' $AegisPort '--role aegis' @{
    Aegis__StaticRoot = "$widget"
    Aegis__Host       = "https://localhost:$AegisPort"
}
