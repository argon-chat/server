#!/usr/bin/env bash
#
# The parts of bootstrap.sh that can be wrong without a machine to install onto.
#
# Not a substitute for running the installer: most of what it does is docker, and that has to be tried
# for real. What is here is the logic that is easy to get wrong and impossible to notice afterwards — a
# code generator that dies of SIGPIPE under `pipefail`, and the question everything else is built from.

set -uo pipefail

# `main` does not run when this is sourced, which is the point. It brings `set -e` with it, which a test
# harness does not want: a failed assertion should be reported and followed by the next one.
# shellcheck source=bootstrap.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/bootstrap.sh"
set +e

failures=0

check() {
    local what=$1 expected=$2 actual=$3

    if [[ $expected == "$actual" ]]; then
        printf '  ok    %s\n' "$what"
    else
        printf '  FAIL  %s\n        expected: %s\n        actual:   %s\n' "$what" "$expected" "$actual"
        failures=$(( failures + 1 ))
    fi
}

# ── answers, survivable across subshells ───────────────────────────────────────────────────────────

# `ask` is called as `DOMAIN=$(ask …)`, which runs it in a subshell — so a counter in a variable is
# incremented in a copy of the shell and the next call gets the same answer again, forever. The queue is
# therefore a file, whose consumption is a real side effect. Getting this wrong the first time hung the
# suite rather than failing it, which is what the watchdog below is for.
QUEUE=$(mktemp)
trap 'rm -f "$QUEUE" "$QUEUE.rest"' EXIT

queue() {
    printf '%s\n' "$@" > "$QUEUE"
}

ask() {
    if [[ ! -s $QUEUE ]]; then
        # `$$` is the original shell even inside a subshell, so this reaches the test run rather than the
        # subshell it is trapped in. Without it an exhausted queue is an infinite loop in the function
        # under test, and the suite hangs instead of reporting.
        printf '\n  FAIL  ran out of answers; the function under test kept asking\n' >&2
        kill "$$"
    fi

    local line
    line=$(head -n 1 "$QUEUE")

    tail -n +2 "$QUEUE" > "$QUEUE.rest" && mv "$QUEUE.rest" "$QUEUE"

    printf '%s' "$line"
}

silently() { "$@" >/dev/null 2>&1; }

# ── the bootstrap code ─────────────────────────────────────────────────────────────────────────────

printf '\nthe bootstrap code\n'

code=$(random_code)

# Four groups of four. The shape matters because this is read off a screen and typed into a browser.
if [[ $code =~ ^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$ ]]; then
    check "is four groups of four" "yes" "yes"
else
    check "is four groups of four" "yes" "no ($code)"
fi

# The alphabet exists so a code photographed off a terminal can be typed back. A generator that quietly
# started emitting I, l, 1, O or 0 would produce codes that look fine and cannot be entered.
check "has no ambiguous characters" "" "$(printf '%s' "$code" | tr -dc 'IlO01')"

# `tr … | head -c` closes the pipe under `tr`, which dies of SIGPIPE, which `pipefail` turns into the
# installer exiting with no output at all. Running it under the settings the script actually runs under
# is the only thing that shows that up — and eight times, because a short read is intermittent.
distinct=$(for _ in 1 2 3 4 5 6 7 8; do (set -euo pipefail; random_code; echo); done | sort -u | wc -l)
check "survives pipefail, eight times over" "8" "$(printf '%s' "$distinct" | tr -d ' ')"

# ── the domain ─────────────────────────────────────────────────────────────────────────────────────

printf '\nthe domain\n'

queue "chat.example.org"
silently ask_domain
check "a hostname is accepted" "chat.example.org" "$DOMAIN"

# Everything downstream is built from this: the certificate, the routing rule, the URL printed at the
# end. A URL pasted in by mistake has to be one clear question rather than three confusing failures.
queue "https://chat.example.org" "chat.example.org"
silently ask_domain
check "a pasted URL is asked again" "chat.example.org" "$DOMAIN"

queue "chat.example.org/panel" "chat.example.org"
silently ask_domain
check "a path is asked again" "chat.example.org" "$DOMAIN"

queue "" "localhost" "chat.example.org"
silently ask_domain
check "an empty answer and a dotless name are asked again" "chat.example.org" "$DOMAIN"

# The address plus the port somebody remembers. Accepted, it would reach the panel as a hostname that
# is not one, and the edge would refuse to be built — three steps after the mistake was made.
queue "chat.example.org:8443" "chat.example.org"
silently ask_domain
check "a port is asked again" "chat.example.org" "$DOMAIN"

# Each of these parses as a hostname to a careless check and to nothing else: a trailing dot, a leading
# hyphen, an underscore, a trailing hyphen on a label.
for bad_name in "chat.example.org." "-chat.example.org" "chat_example.org" "chat-.example.org"; do
    queue "$bad_name" "chat.example.org"
    silently ask_domain
    check "$bad_name is asked again" "chat.example.org" "$DOMAIN"
done

# ── the install root ───────────────────────────────────────────────────────────────────────────────

printf '\nthe install root\n'

SUDO=''

root=$(mktemp -d)
queue "$root/instance"
silently ask_root
check "a directory that does not exist yet is created" "yes" "$([[ -d $root/instance ]] && echo yes || echo no)"

# A path that exists and is not a directory. `mkdir -p` reports success for an existing *file* on some
# platforms and failure on others, so the check is explicit rather than relying on which one this is —
# and an install root that is really a file would otherwise fail later, while writing the compose
# document, with an error naming a path the operator never typed.
: > "$root/a-file"
queue "$root/a-file"
( silently ask_root )
check "a path that is not a directory stops the install" "1" "$?"

rm -rf "$root"

# ── the front door's arguments ─────────────────────────────────────────────────────────────────────

printf '\nwhat gets handed to the image\n'

# `emit_project` is the seam between the shell and everything that knows about Argon, so what matters
# is that each answer reaches it and that the optional ones appear only when they were given. Docker is
# replaced by an echo; the assertion is on the command line that would have been run.
captured=''

# shellcheck disable=SC2317
docker() { captured="$*"; }

ROOT=/opt/argon
DOMAIN=chat.example.org
PANEL_IMAGE=ghcr.io/argon-chat/bootstrapper@sha256:abc
TRAFFIC=lets-encrypt
TLS_CERT=''
TLS_KEY=''
ACME_EMAIL=''

silently emit_project
check "no certificate flags on the ACME path" "" "$(printf '%s' "$captured" | grep -o -- '--tls-cert' || true)"
check "no empty --acme-email" "" "$(printf '%s' "$captured" | grep -o -- '--acme-email' || true)"
check "the domain is passed" "--domain=chat.example.org" "$(printf '%s' "$captured" | grep -o -- '--domain=[^ ]*' || true)"
check "the panel image is passed as pulled" "--panel-image=$PANEL_IMAGE" "$(printf '%s' "$captured" | grep -o -- '--panel-image=[^ ]*' || true)"

ACME_EMAIL=ops@example.org
silently emit_project
check "an address that was given is passed" "--acme-email=ops@example.org" "$(printf '%s' "$captured" | grep -o -- '--acme-email=[^ ]*' || true)"

TRAFFIC=own-certificate
ACME_EMAIL=''
TLS_CERT=/etc/argon/tls.crt
TLS_KEY=/etc/argon/tls.key
silently emit_project
check "both halves of a pair are passed" "--tls-cert=/etc/argon/tls.crt --tls-key=/etc/argon/tls.key" \
    "$(printf '%s' "$captured" | grep -o -- '--tls-cert=[^ ]* --tls-key=[^ ]*' || true)"

printf '\n'

if (( failures )); then
    printf '%s failure(s)\n' "$failures"
    exit 1
fi

printf 'all good\n'
