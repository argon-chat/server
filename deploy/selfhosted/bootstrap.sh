#!/usr/bin/env bash
#
# Argon self-hosted — bootstrap.
#
# The one command an operator runs on an empty machine. It checks the machine, asks the few questions
# that have to be answered before anything can serve, brings up the front door with the setup panel
# behind it, and hands over to a browser. Everything else — which version, which roles, object storage,
# the first account — is answered there, over the TLS this script established.
#
#   ./bootstrap.sh
#
# What it does NOT do is configure Argon. It knows about domains, certificates and docker; it knows
# nothing about spaces, channels or storage buckets, and it never edits a configuration file. That is
# the panel's job, and the panel is a container this script starts.
#
# Traefik comes up first, before any of that: the panel is behind a proxy from its first second and
# never holds a public port. The alternative — the panel binding :443 and handing it over when setup
# finishes — has to answer a request while closing the listener that carried it, and on the Let's
# Encrypt path needs a second ACME client here to obtain the certificate Traefik then obtains again.
#
# Safe to run twice. Answering the same questions produces the same project, and compose reconciles it.

set -euo pipefail

# Helpers, the machine report, and the colours, from the script that owns them. Sourced rather than
# copied: two implementations of "ask a yes/no question on a tty" is two chances to get the redirection
# order wrong, and that one has already been got right once.
HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
readonly HERE
# shellcheck source=preflight.sh
source "$HERE/preflight.sh"

# The panel's image. A tag is resolved to a digest after the pull, so the compose file names exactly
# what this run fetched rather than whatever the tag points at when a container is next recreated.
readonly BOOTSTRAPPER_IMAGE=${ARGON_BOOTSTRAPPER_IMAGE:-ghcr.io/argon-chat/bootstrapper:latest}

readonly DEFAULT_ROOT=/opt/argon
readonly PANEL_PATH=/panel
readonly TUNNEL_PORT=8080
readonly CODE_FILE=bootstrap.code

# How long to wait for Let's Encrypt. Issuance is usually seconds; the ceiling is for the case where it
# is never going to work — a DNS record that does not point here — and the point of a ceiling is to
# stop waiting and say what to check, rather than to hang on a machine nobody is watching.
readonly ACME_TIMEOUT=180

# ── asking ─────────────────────────────────────────────────────────────────────────────────────────

# One line of input, with a default. Reads the terminal directly, like `confirm`, so this still works
# when stdin is the installer itself being piped in from curl.
ask() {
    local prompt=$1 fallback=${2:-} answer
    local suffix=${fallback:+ [$fallback]}

    read -r -p "$prompt$suffix: " answer 2>/dev/null </dev/tty || answer=''

    printf '%s' "${answer:-$fallback}"
}

# A numbered choice. The menu goes to stderr so that the chosen value is this function's only output and
# can be captured — a menu on stdout would end up inside the variable.
choose() {
    local title=$1
    shift

    local -a labels=() values=()
    local index

    while (( $# )); do
        values+=("$1")
        labels+=("$2")
        shift 2
    done

    printf '\n%s%s%s\n' "$BOLD" "$title" "$OFF" >&2

    for index in "${!labels[@]}"; do
        printf '  %s) %s\n' "$(( index + 1 ))" "${labels[$index]}" >&2
    done

    local answer

    while true; do
        read -r -p "choice [1-${#values[@]}]: " answer 2>/dev/null </dev/tty || answer=''

        if [[ $answer =~ ^[0-9]+$ ]] && (( answer >= 1 && answer <= ${#values[@]} )); then
            printf '%s' "${values[$(( answer - 1 ))]}"
            return 0
        fi

        printf '  %snot one of the choices%s\n' "$YELLOW" "$OFF" >&2
    done
}

# A file that exists and can be read, asked for until it is one. An operator who mistypes a certificate
# path finds out here, where the answer is to type it again, rather than after the edge is running and
# every handshake fails for a reason that reads as a network problem.
ask_existing_file() {
    local prompt=$1 path

    while true; do
        path=$(ask "$prompt")

        if [[ -z $path ]]; then
            printf '  %sa path is required%s\n' "$YELLOW" "$OFF" >&2
            continue
        fi

        if [[ ! -r $path ]]; then
            printf '  %s%s cannot be read%s\n' "$YELLOW" "$path" "$OFF" >&2
            continue
        fi

        printf '%s' "$path"
        return 0
    done
}

# ── the bootstrap code ─────────────────────────────────────────────────────────────────────────────

# The credential that gets the operator into the panel and nobody else.
#
# No ambiguous characters, because this is read off a terminal and typed into a browser, sometimes from
# a photograph of a screen: I/l/1 and O/0 are the same glyph in enough fonts to matter. Sixteen
# characters from a thirty-two character alphabet is eighty bits, against a panel that locks out after
# five wrong answers — the margin is not the interesting part here, the file mode is.
random_code() {
    local pool='ABCDEFGHJKLMNPQRSTUVWXYZ23456789' out=''

    # `head` first and `tr` second, never the other way round: `tr ... | head -c` closes the pipe under
    # the reader, `tr` dies of SIGPIPE, and `pipefail` turns that into the whole installer exiting with
    # no message at all. Looped because a filtered block can come up short.
    while (( ${#out} < 16 )); do
        out+=$(head -c 512 /dev/urandom | LC_ALL=C tr -dc "$pool")
    done

    printf '%s-%s-%s-%s' "${out:0:4}" "${out:4:4}" "${out:8:4}" "${out:12:4}"
}

# ── docker ─────────────────────────────────────────────────────────────────────────────────────────

compose() {
    $SUDO docker compose --project-directory "$ROOT" "$@"
}

# The image as a digest, so the compose file names exactly what was pulled. Falls back to the tag when
# there is no digest to be had — an image built locally has none, and refusing that would make this
# script unusable for the people developing it.
resolve_image() {
    local reference=$1 digest

    digest=$($SUDO docker image inspect --format '{{if .RepoDigests}}{{index .RepoDigests 0}}{{end}}' "$reference" 2>/dev/null || true)

    printf '%s' "${digest:-$reference}"
}

# ── the questions ──────────────────────────────────────────────────────────────────────────────────

ask_domain() {
    while true; do
        DOMAIN=$(ask "The name this instance will answer to (e.g. chat.example.org)")

        # Refused here rather than passed on, because everything downstream is built from it — the
        # certificate, the routing rule, the URL printed at the end. The same rule the panel applies
        # when it builds the edge, applied while the operator is still standing in front of it: caught
        # there, it is an install that has already started failing; caught here, it is one more line.
        #
        # A scheme and a port get their own message before the general one. They are the two mistakes
        # somebody makes on purpose — pasting the address they use, or the address plus the port they
        # remember — and "letters, digits and dots" is a poor answer to either.
        if [[ $DOMAIN == *://* ]]; then
            warn "that is a URL; this wants just the hostname"
            continue
        fi

        if [[ $DOMAIN == *:* ]]; then
            warn "no port here — the front door answers on 443"
            continue
        fi

        if [[ $DOMAIN =~ ^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$ ]]; then
            return 0
        fi

        warn "a hostname is required: letters, digits, hyphens and at least one dot"
    done
}

ask_traffic() {
    local acme_note='' own_note=''

    # A machine behind NAT cannot answer an ACME challenge on its own address, so recommending that
    # path there would be recommending the one option guaranteed to fail on this box.
    if [[ ${NETWORK_SHAPE:-} == public ]]; then
        acme_note=' (recommended)'
    else
        own_note=' (recommended — this machine has no public address)'
    fi

    TRAFFIC=$(choose "How should this instance be reached?" \
        lets-encrypt       "Let's Encrypt — this machine is public and gets its own certificate$acme_note" \
        own-certificate    "I have a certificate and key on this machine$own_note" \
        cloudflare-proxied "Behind Cloudflare's proxy, with an origin certificate on this machine" \
        cloudflare-tunnel  "Behind a Cloudflare tunnel — nothing of this machine is exposed")

    case $TRAFFIC in
        lets-encrypt)
            note "Let's Encrypt will be asked for a certificate as soon as the door is up. That needs"
            note "$DOMAIN to already resolve to this machine, and port 443 to reach it."
            ACME_EMAIL=$(ask "Address for expiry warnings from Let's Encrypt (optional)")
            ;;

        own-certificate | cloudflare-proxied)
            if [[ $TRAFFIC == cloudflare-proxied ]]; then
                note "Cloudflare terminates TLS for your visitors; this certificate is the one it uses to"
                note "reach this machine. A Cloudflare Origin CA certificate is the usual answer."
            fi

            TLS_CERT=$(ask_existing_file "Path to the certificate (PEM, full chain)")
            TLS_KEY=$(ask_existing_file "Path to the private key (PEM)")
            ;;

        cloudflare-tunnel)
            note "Nothing will be published on this machine's public interfaces. The front door listens"
            note "on 127.0.0.1:$TUNNEL_PORT, and your tunnel has to point $DOMAIN at it."
            note "This script does not run cloudflared — it does not know your tunnel's credentials."
            ;;
    esac
}

ask_root() {
    ROOT=$(ask "Where should this instance keep its configuration" "$DEFAULT_ROOT")

    # Created now rather than by the emit step, so that a path the operator cannot write is a question
    # they are still standing in front of.
    $SUDO mkdir -p "$ROOT" || die "cannot create $ROOT"

    [[ -d $ROOT ]] || die "$ROOT is not a directory"
}

# ── doing it ───────────────────────────────────────────────────────────────────────────────────────

pull_panel() {
    heading "The panel"

    say "pulling $BOOTSTRAPPER_IMAGE"

    $SUDO docker pull "$BOOTSTRAPPER_IMAGE" >/dev/null || die "could not pull $BOOTSTRAPPER_IMAGE"

    PANEL_IMAGE=$(resolve_image "$BOOTSTRAPPER_IMAGE")

    ok "panel image $PANEL_IMAGE"
}

# Asks the image to write the compose project for the front door. See `emit.ts` for why: this script
# does not know the project name, the network subnet, or how a traffic shape becomes Traefik
# configuration — and every one of those has to match what the panel writes later, or the handover
# builds a second project beside this one instead of replacing it.
emit_project() {
    heading "The front door"

    local -a arguments=(
        "--domain=$DOMAIN"
        "--traffic=$TRAFFIC"
        "--panel-image=$PANEL_IMAGE"
        "--root=$ROOT"
    )

    if [[ -n ${TLS_CERT:-} ]]; then
        arguments+=("--tls-cert=$TLS_CERT" "--tls-key=$TLS_KEY")
    fi

    if [[ -n ${ACME_EMAIL:-} ]]; then
        arguments+=("--acme-email=$ACME_EMAIL")
    fi

    $SUDO docker run --rm \
        -v "$ROOT:/argon" \
        -e ARGON_BOOTSTRAP_CONFIG_DIR=/argon \
        "$PANEL_IMAGE" emit-bootstrap "${arguments[@]}" ||
        die "the panel image refused to describe this install"
}

write_code() {
    CODE=$(random_code)

    # The mode is set before there is anything in the file, rather than after. The panel refuses to
    # start when this file is readable by anyone else, and it is right to: until setup finishes, this
    # string is the credential to this machine.
    $SUDO install -m 600 /dev/null "$ROOT/$CODE_FILE"

    printf '%s\n' "$CODE" | $SUDO tee "$ROOT/$CODE_FILE" >/dev/null
}

start() {
    say "starting the door and the panel"

    compose up -d || die "compose refused to start the project; see the output above"
}

# Waits for the panel to answer *through the edge*, which is what proves the two are wired together
# rather than merely both running.
wait_for_panel() {
    local deadline=$(( SECONDS + 60 ))
    local port=443 scheme=https

    if [[ $TRAFFIC == cloudflare-tunnel ]]; then
        port=$TUNNEL_PORT
        scheme=http
    fi

    while (( SECONDS < deadline )); do
        # `-k` deliberately: on the ACME path the certificate may not have been issued yet, and this
        # check is about routing. Whether the certificate is real is the next check, kept separate so
        # that a failure says which of the two went wrong.
        if curl -sfk --max-time 5 -H "Host: $DOMAIN" "$scheme://127.0.0.1:$port/api/health" >/dev/null 2>&1; then
            ok "the panel is answering behind the front door"
            return 0
        fi

        sleep 2
    done

    bad "the panel did not answer through the front door within a minute"
    note "      $SUDO docker compose --project-directory $ROOT logs"

    return 1
}

# Only on the ACME path, and only as a report: every other shape has its certificate the moment the
# door starts, and this is the one where that takes a little while and can fail for reasons outside
# this machine.
wait_for_certificate() {
    [[ $TRAFFIC == lets-encrypt ]] || return 0

    say "waiting for Let's Encrypt (up to ${ACME_TIMEOUT}s)"

    local deadline=$(( SECONDS + ACME_TIMEOUT ))

    while (( SECONDS < deadline )); do
        # No `-k`. That is the whole point of this check: a certificate a browser would accept, fetched
        # the way a browser would fetch it.
        if curl -sf --max-time 5 "https://$DOMAIN$PANEL_PATH/api/health" >/dev/null 2>&1; then
            ok "certificate issued for $DOMAIN"
            return 0
        fi

        sleep 3
    done

    warn "no certificate yet after ${ACME_TIMEOUT}s"
    note "      the three things this needs, in the order they fail:"
    note "        - $DOMAIN resolves to this machine   (dig +short $DOMAIN)"
    note "        - port 443 reaches it from outside   (nothing else may be bound to it)"
    note "        - nothing proxies it   (the challenge is TLS-ALPN on 443, and a CDN answers that itself)"
    note "      the door is up and keeps retrying; nothing here needs to be run again."
}

report() {
    heading "Ready"

    say ''
    say "  Open   ${BOLD}https://$DOMAIN$PANEL_PATH${OFF}"
    say "  Code   ${BOLD}$CODE${OFF}"
    say ''

    note "The code is in $ROOT/$CODE_FILE, readable by root only. It stops working once setup finishes."

    if [[ $TRAFFIC == cloudflare-tunnel ]]; then
        say ''
        note "Point your tunnel at http://127.0.0.1:$TUNNEL_PORT for $DOMAIN before opening that link."
    fi
}

main() {
    heading "Argon self-hosted"
    note "This checks the machine, asks three questions, and starts the setup panel."

    preflight_report

    heading "This instance"
    ask_domain
    ask_traffic
    ask_root

    pull_panel
    emit_project
    write_code
    start

    heading "Handover"
    wait_for_panel
    wait_for_certificate
    report
}

# Guarded for the same reason preflight.sh is: sourced, this file defines its helpers and does nothing,
# which is what bootstrap.test.sh needs in order to exercise them without a machine to install onto.
if [[ ${BASH_SOURCE[0]} == "${0}" ]]; then
    main "$@"
fi
