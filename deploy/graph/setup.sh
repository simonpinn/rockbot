#!/usr/bin/env bash
# Runs the whole Graph setup (mail, calendar, contacts): app registration, mailbox scoping,
# verification, and compose wiring. Every step is idempotent, so re-run it whenever you like.
#
# Usage: setup.sh --mailbox you@domain [--other-user someone@domain] [--app-name NAME] [--rotate]
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

create_args=()
verify_args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --mailbox|--app-name|--secret-years) create_args+=("$1" "$2"); shift 2 ;;
    --rotate) create_args+=("$1"); shift ;;
    --other-user) verify_args+=("$1" "$2"); shift 2 ;;
    -h|--help) sed -n '2,5p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

log "Step 1/4: Entra app registration"
"$GRAPH_DIR/01-create-app.sh" "${create_args[@]}"

log "Step 2/4: Exchange mailbox scoping"
"$GRAPH_DIR/02-scope-mailbox.sh"

log "Step 3/4: verification"
if ! "$GRAPH_DIR/03-verify.sh" "${verify_args[@]}"; then
  warn "Verification failed. A 403 on your own mailbox right after the first run is normal:"
  warn "Exchange can take up to 2 hours to apply. Re-run deploy/graph/03-verify.sh later,"
  warn "then deploy/graph/04-enable-compose.sh."
  exit 1
fi

log "Step 4/4: compose wiring"
"$GRAPH_DIR/04-enable-compose.sh"
