#!/usr/bin/env bash
# Starts the HTTPS front end and checks it end to end. Safe to re-run.
#   - adds "https" to COMPOSE_PROFILES in deploy/docker-compose/.env
#   - (re)starts caddy and blazor with the new settings
#   - waits for the Let's Encrypt certificate, then checks /healthz and that / asks for sign-in
#
# Usage: 03-enable.sh
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[[ "${1:-}" != "-h" && "${1:-}" != "--help" ]] || { sed -n '2,8p' "$0"; exit 0; }

require docker curl
host="$(env_get RB_PUBLIC_HOST)"
[[ -n "$host" ]] || die "RB_PUBLIC_HOST is not set; run 01-dns.sh first"
[[ -n "$(env_get BLAZOR_AUTH_MICROSOFT_CLIENT_ID)" ]] || die "Microsoft sign-in isn't configured; run 02-signin-app.sh first"

enable_profile https
log "Building and starting caddy and blazor"
"${COMPOSE[@]}" up -d --build caddy blazor

# --resolve pins the name to this machine, so the check works even if the LAN resolver blocks
# the public name; curl still verifies the certificate against it.
probe() { curl -sS -o /dev/null -w '%{http_code} %{redirect_url}' --resolve "$host:443:127.0.0.1" "https://$host$1" 2>/dev/null || true; }

log "Waiting for the certificate (Let's Encrypt DNS-01 usually takes 30-90 seconds)"
for _ in $(seq 1 36); do
  [[ "$(probe /healthz)" == "200 "* ]] && break
  sleep 5
done
if [[ "$(probe /healthz)" != "200 "* ]]; then
  "${COMPOSE[@]}" logs --tail 20 caddy >&2
  die "https://$host/healthz isn't answering with a trusted certificate yet; see the caddy log above"
fi
log "PASS: https://$host serves a trusted certificate"

read -r status location <<<"$(probe /)"
if [[ "$status" == 302 && "$location" == */login* ]]; then
  log "PASS: https://$host/ redirects to sign-in"
else
  die "expected / to redirect to /login, got '$status $location'"
fi

log "Open https://$host from any device on your LAN and sign in with Microsoft."
