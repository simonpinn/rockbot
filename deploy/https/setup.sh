#!/usr/bin/env bash
# HTTPS + Microsoft sign-in for LAN access to the Blazor UI: DNS, sign-in app, then the Caddy
# front end. Every step is idempotent, so re-run it whenever you like. Stops after the DNS step
# until the zone is delegated at your registrar.
#
# Usage: setup.sh --zone home.example.com [--host rockbot] [--allow-email you@domain] [--rotate]
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

dns_args=()
signin_args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --zone|--host|--ip|--resource-group|--location) dns_args+=("$1" "$2"); shift 2 ;;
    --allow-email) signin_args+=("$1" "$2"); shift 2 ;;
    --rotate) dns_args+=("$1"); signin_args+=("$1"); shift ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

log "Step 1/3: Azure DNS"
status=0
"$HTTPS_DIR/01-dns.sh" "${dns_args[@]}" || status=$?
if (( status == EXIT_NOT_DELEGATED )); then
  log "Add the NS records above, wait a few minutes, then re-run deploy/https/setup.sh."
  exit "$status"
fi
(( status == 0 )) || exit "$status"

log "Step 2/3: Microsoft sign-in app"
"$HTTPS_DIR/02-signin-app.sh" "${signin_args[@]}"

log "Step 3/3: HTTPS front end"
"$HTTPS_DIR/03-enable.sh"

warn "deploy/docker-compose/.env now holds new secrets and settings (RB_*, CADDY_AZURE_*, BLAZOR_AUTH_*)."
warn "If chezmoi manages that file, copy them into your template/Bitwarden before the next 'chezmoi apply'."
