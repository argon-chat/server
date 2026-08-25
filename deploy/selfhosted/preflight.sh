#!/usr/bin/env bash
#
# Argon self-hosted — preflight.
#
# Looks at the machine, reports what it found, and installs nothing without being told to. This is the
# half of the installer that can be wrong about the world; the half that configures Argon runs after it
# and asks the server binary itself what it needs.
#
# Run it directly:
#   ./preflight.sh
#
# It is safe to run twice. Nothing here is idempotent-by-luck: every step either reports or asks.

set -euo pipefail

readonly ARGON_MIN_DOCKER_MAJOR=24

# Colour only when a terminal is watching. A log file or a pipe gets plain text, because escape codes in
# a pasted bug report are worse than no colour at all.
if [[ -t 1 ]]; then
    readonly DIM=$'\033[2m' BOLD=$'\033[1m' RED=$'\033[31m' YELLOW=$'\033[33m' GREEN=$'\033[32m' OFF=$'\033[0m'
else
    readonly DIM='' BOLD='' RED='' YELLOW='' GREEN='' OFF=''
fi

say()   { printf '%s\n' "$*"; }
note()  { printf '%s%s%s\n' "$DIM" "$*" "$OFF"; }
ok()    { printf '  %s✓%s %s\n' "$GREEN" "$OFF" "$*"; }
warn()  { printf '  %s!%s %s\n' "$YELLOW" "$OFF" "$*"; }
bad()   { printf '  %s✗%s %s\n' "$RED" "$OFF" "$*"; }
die()   { printf '\n%serror:%s %s\n' "$RED" "$OFF" "$*" >&2; exit 1; }

heading() { printf '\n%s%s%s\n' "$BOLD" "$*" "$OFF"; }

# Asks a yes/no question, defaulting to no. Defaulting to no because every caller of this is about to
# change the machine, and a script that installs things when somebody holds Enter is a bad neighbour.
confirm() {
    local answer
    # Silenced, and a failure is a "no": without a terminal there is nobody to ask, and the caller is
    # always about to change the machine. A script run from cron or a pipe should do nothing, quietly.
    # stderr is redirected BEFORE stdin, and the order is the whole point: redirections are applied left
    # to right, so a /dev/tty that cannot be opened reports through whatever stderr is at that moment.
    # Put it the other way round and the failure prints before the silencer exists.
    read -r -p "$1 [y/N] " answer 2>/dev/null </dev/tty || return 1
    [[ $answer == [yY] || $answer == [yY][eE][sS] ]]
}

# ── the machine ────────────────────────────────────────────────────────────────────────────────────

require_linux() {
    [[ $(uname -s) == Linux ]] || die "this installs on Linux; found $(uname -s)."

    # x86_64 only, because that is what is published: `.github/workflows/publish.yml` builds a single
    # platform. Listing aarch64 here as well used to make this a promise nobody kept — the machine
    # passed preflight and then failed on the first `docker pull`, with an error about a manifest
    # rather than about the architecture.
    case $(uname -m) in
        x86_64) ;;
        *) die "unsupported architecture $(uname -m); Argon images are published for x86_64 only." ;;
    esac
}

# Distribution, from the file every modern distribution agrees on.
detect_distro() {
    if [[ -r /etc/os-release ]]; then
        # shellcheck disable=SC1091
        . /etc/os-release
        DISTRO_ID=${ID:-unknown}
        DISTRO_NAME=${PRETTY_NAME:-$DISTRO_ID}
    else
        DISTRO_ID=unknown
        DISTRO_NAME="unknown (no /etc/os-release)"
    fi
}

# The package manager, by which binary exists rather than by which distribution said it is. A derivative
# nobody has heard of still answers this correctly if it kept its parent's tooling.
detect_package_manager() {
    for candidate in apt-get dnf yum apk pacman zypper; do
        if command -v "$candidate" >/dev/null 2>&1; then
            PKG=$candidate
            return
        fi
    done

    PKG=''
}

# Root, or a way to become it. Reported rather than demanded: the checks below all run unprivileged, and
# only an install needs this.
detect_privilege() {
    if [[ $EUID -eq 0 ]]; then
        SUDO=''
        PRIVILEGE='root'
    elif command -v sudo >/dev/null 2>&1; then
        SUDO='sudo'
        PRIVILEGE='sudo'
    else
        SUDO=''
        PRIVILEGE='none'
    fi
}

# ── tools ──────────────────────────────────────────────────────────────────────────────────────────

# What the installer needs, and what each is for. The reason travels with the name so a refusal can say
# why rather than just what.
tool_reason() {
    case $1 in
        docker)  echo "runs Argon and everything it depends on" ;;
        curl)    echo "fetches images metadata and checks whether this machine is reachable" ;;
        *)       echo "required" ;;
    esac
}

package_for() {
    case "$1:$PKG" in
        docker:apt-get) echo "docker.io" ;;
        docker:*)       echo "docker" ;;
        *)              echo "$1" ;;
    esac
}

install_command() {
    local package=$1
    # Prefixed only when there is something to prefix with, so the command printed for a root shell does
    # not carry a stray double space through a copy-paste.
    local as=${SUDO:+$SUDO }

    case $PKG in
        apt-get) echo "${as}apt-get update && ${as}apt-get install -y $package" ;;
        dnf|yum) echo "${as}$PKG install -y $package" ;;
        apk)     echo "${as}apk add --no-cache $package" ;;
        pacman)  echo "${as}pacman -Sy --noconfirm $package" ;;
        zypper)  echo "${as}zypper install -y $package" ;;
        *)       echo '' ;;
    esac
}

# Offers to install one missing tool, printing the exact command first.
#
# Printed rather than run silently on purpose: this script is the kind of thing people paste from a
# README into a root shell, and the least it can do is show what it is about to do to their machine.
offer_install() {
    local tool=$1 package command

    package=$(package_for "$tool")
    command=$(install_command "$package")

    if [[ -z $command ]]; then
        bad "$tool is missing and this script does not know how to install it here"
        note "      install $package yourself, then run this again"
        return 1
    fi

    if [[ $PRIVILEGE == none ]]; then
        bad "$tool is missing and installing it needs root"
        note "      run this as root, or install it yourself: $command"
        return 1
    fi

    warn "$tool is missing — $(tool_reason "$tool")"
    note "      $command"

    if confirm "      install it now?"; then
        eval "$command" || die "installing $package failed; nothing else was changed."
        ok "$tool installed"
        return 0
    fi

    bad "$tool is missing and was not installed"
    return 1
}

check_tools() {
    local missing=0

    # openssl was on this list, and refusing without it turned a machine that would have worked fine
    # into "not ready". Nothing uses it: the bootstrap code comes from /dev/urandom, and every key and
    # password this instance runs on is minted by the panel, inside its container, where the crypto is
    # the runtime's. The reason string that travelled with it — "generates the keys and passwords this
    # instance will use" — described an earlier design in which this script did that.
    for tool in docker curl; do
        if command -v "$tool" >/dev/null 2>&1; then
            ok "$tool"
        else
            offer_install "$tool" || missing=1
        fi
    done

    return $missing
}

# Docker has to be present, recent enough, and actually running — three different failures with three
# different fixes, so they are reported separately rather than as "docker is broken".
check_docker() {
    command -v docker >/dev/null 2>&1 || return 1

    local version major
    version=$(docker version --format '{{.Server.Version}}' 2>/dev/null || true)

    if [[ -z $version ]]; then
        bad "docker is installed but its daemon is not answering"
        note "      try: $SUDO systemctl enable --now docker"
        note "      if you are not in the docker group, this also looks like a daemon that is down"
        return 1
    fi

    major=${version%%.*}

    if (( major < ARGON_MIN_DOCKER_MAJOR )); then
        bad "docker $version is older than $ARGON_MIN_DOCKER_MAJOR, which is the oldest we test against"
        return 1
    fi

    ok "docker $version"

    if docker compose version >/dev/null 2>&1; then
        ok "docker compose $(docker compose version --short 2>/dev/null || echo 'plugin')"
    else
        bad "the docker compose plugin is missing"
        note "      Argon runs as a compose project; the standalone docker-compose script is not enough"
        return 1
    fi
}

# ── the network ────────────────────────────────────────────────────────────────────────────────────

# The address this machine uses to reach the outside world. Read from the routing table rather than from
# a guess about interface names: `ip route get` answers the question actually being asked, which is
# "which of my addresses would a packet leave from", and it is right on a machine with six interfaces.
local_address() {
    # Guarded, because a minimal image can genuinely be without iproute2 — and an unguarded `ip` here is
    # a 127 that `set -e` turns into the script dying halfway through its own report, which is how this
    # was found. `hostname -I` is the fallback and is less precise: it lists every address, so the first
    # one is a guess where `ip route get` was an answer.
    if command -v ip >/dev/null 2>&1; then
        ip route get 1.1.1.1 2>/dev/null |
            awk '{ for (i = 1; i < NF; i++) if ($i == "src") { print $(i + 1); exit } }'
    elif command -v hostname >/dev/null 2>&1; then
        hostname -I 2>/dev/null | awk '{ print $1 }'
    fi
}

# The address the world sees. Asked of an external service, so it is behind a prompt: a machine that is
# being installed offline should not have its first act be an unannounced call to somebody else's server.
public_address() {
    command -v curl >/dev/null 2>&1 || return 0
    curl --silent --max-time 8 https://api.ipify.org 2>/dev/null || true
}

check_network() {
    local local_ip public_ip

    local_ip=$(local_address)

    if [[ -n $local_ip ]]; then
        ok "local address $local_ip"
    else
        warn "could not read a local address from the routing table"
    fi

    if ! confirm "      ask an external service what this machine's public address is?"; then
        note "      skipped — assuming no public address"
        NETWORK_SHAPE=private
        return 0
    fi

    public_ip=$(public_address)

    if [[ -z $public_ip ]]; then
        warn "no answer — treating this as a machine without a public address"
        NETWORK_SHAPE=private
        return 0
    fi

    if [[ $public_ip == "$local_ip" ]]; then
        ok "public address $public_ip, and it is this machine's own"
        NETWORK_SHAPE=public
    else
        ok "public address $public_ip, reached through NAT from $local_ip"
        NETWORK_SHAPE=nat
    fi
}

# ── resources ──────────────────────────────────────────────────────────────────────────────────────

# Reported, never enforced. Argon runs several containers and a database, and a machine that is too small
# will fail in ways that look like bugs — but the numbers below are a shape, not a specification, and a
# script that refuses to install on a box somebody knows the size of would be wrong more often than right.
check_resources() {
    local memory_kb memory_gb cores space_gb

    memory_kb=$(awk '/^MemTotal:/ { print $2 }' /proc/meminfo 2>/dev/null || echo 0)
    memory_gb=$(( memory_kb / 1024 / 1024 ))
    cores=$(nproc 2>/dev/null || echo '?')
    # `df -P` and a column, rather than `--output=avail` and a unit suffix. The GNU flags are absent on
    # busybox, and with `pipefail` a df that fails takes the whole pipeline down with it — which under
    # `set -e` killed this script mid-report on Alpine. POSIX output is 1K blocks, hence the division.
    space_gb=$({ df -P /var/lib 2>/dev/null || true; } | awk 'NR == 2 { printf "%d", $4 / 1024 / 1024 }')
    space_gb=${space_gb:-0}

    if (( memory_gb >= 8 )); then
        ok "${memory_gb}G memory, ${cores} core(s)"
    else
        warn "${memory_gb}G memory, ${cores} core(s) — Argon and its dependencies want 8G comfortably"
    fi

    if (( space_gb >= 40 )); then
        ok "${space_gb}G free on /var/lib"
    else
        warn "${space_gb}G free on /var/lib — images alone take about 10G, and the database grows"
    fi
}

# ── report ─────────────────────────────────────────────────────────────────────────────────────────

# Renamed from `main` so that bootstrap.sh can source this file for its helpers and its machine report
# without the two scripts fighting over the name.
preflight_report() {
    say "${BOLD}Argon self-hosted — preflight${OFF}"
    note "Nothing is installed or configured without asking."

    require_linux
    detect_distro
    detect_package_manager
    detect_privilege

    heading "Machine"
    ok "$DISTRO_NAME ($(uname -m))"

    if [[ -n $PKG ]]; then
        ok "package manager $PKG"
    else
        warn "no known package manager — anything missing will have to be installed by hand"
    fi

    case $PRIVILEGE in
        root) ok "running as root" ;;
        sudo) ok "sudo is available" ;;
        none) warn "not root and no sudo — installing anything will fail" ;;
    esac

    heading "Tools"
    local tools_ok=0
    check_tools || tools_ok=1
    check_docker || tools_ok=1

    heading "Network"
    NETWORK_SHAPE=unknown
    check_network

    heading "Resources"
    check_resources

    heading "Result"

    if (( tools_ok != 0 )); then
        bad "not ready — fix what is marked above and run this again"
        exit 1
    fi

    ok "this machine can run Argon"

    case $NETWORK_SHAPE in
        public)
            note "It has a public address, so the setup can obtain a real certificate for it."
            ;;
        nat|private)
            note "It has no public address of its own, so Let's Encrypt cannot reach it to issue a"
            note "certificate. Bring your own, or use a Cloudflare tunnel — that path needs nothing"
            note "reachable from outside at all."
            ;;
    esac
}

# Only when this file is what was run. Sourced — which is how bootstrap.sh reuses everything above —
# nothing happens until the caller asks for it.
#
# The closing directions live here rather than in the report for the same reason: run by hand, the next
# step is the installer; run *as part of* the installer, there is no next step to name and saying there
# is one sends the operator looking for a second command that does not exist.
if [[ ${BASH_SOURCE[0]} == "${0}" ]]; then
    preflight_report "$@"

    say ''
    note "Next: ./bootstrap.sh, which asks what this instance should be and starts it."
    note "It runs these same checks first, so nothing here has to be repeated by hand."
fi
