#!/usr/bin/env bash
# Entra ID sign-in for the Blazor UI. Safe to re-run.
#   - "RockBot Sign-in" app registration: single tenant, web redirect
#     https://$RB_PUBLIC_HOST/signin-microsoft, email optional claim, openid/profile/email
#     delegated permissions with admin consent, and a client secret
#   - writes BLAZOR_AUTH_* (enabled, public base URL, Microsoft provider, allowed email) to
#     deploy/docker-compose/.env
#
# Usage: 02-signin-app.sh [--allow-email you@domain] [--rotate]
#   --allow-email defaults to the stored allowlist, then to the signed-in Azure CLI user.
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

allow_email="$(env_get BLAZOR_AUTH_ALLOWED_EMAILS)"
rotate=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --allow-email) allow_email="$2"; shift 2 ;;
    --rotate) rotate=true; shift ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

require az jq
host="$(env_get RB_PUBLIC_HOST)"
[[ -n "$host" ]] || die "RB_PUBLIC_HOST is not set; run 01-dns.sh first"
redirect="https://$host/signin-microsoft"

ensure_az_login
tenant="$(az account show --query tenantId -o tsv)"
allow_email="${allow_email:-$(az account show --query user.name -o tsv)}"
[[ "$allow_email" == *@* ]] || die "--allow-email must be an email address"

ensure_app "$SIGNIN_APP_NAME" --web-redirect-uris "$redirect"

if [[ "$(az ad app show --id "$APP_ID" --query 'web.redirectUris' -o tsv)" != "$redirect" ]]; then
  az ad app update --id "$APP_ID" --web-redirect-uris "$redirect"
  log "Set redirect URI to $redirect"
fi

if ! az ad app show --id "$APP_ID" --query 'optionalClaims.idToken[].name' -o tsv | grep -qx email; then
  az ad app update --id "$APP_ID" --optional-claims '{"idToken":[{"name":"email","essential":false}]}'
  log "Added the email optional claim to the ID token"
fi

graph_app=00000003-0000-0000-c000-000000000000
wanted="$(az ad sp show --id "$graph_app" \
  --query "oauth2PermissionScopes[?value=='openid' || value=='profile' || value=='email'].id" -o tsv | sort)"
have="$(az ad app show --id "$APP_ID" \
  --query "requiredResourceAccess[?resourceAppId=='$graph_app'].resourceAccess[].id" -o tsv | sort)"
if [[ "$(comm -23 <(echo "$wanted") <(echo "$have"))" != "" ]]; then
  mapfile -t scopes <<<"$wanted"
  az ad app permission add --id "$APP_ID" --api "$graph_app" --api-permissions "${scopes[@]/%/=Scope}" 2>/dev/null
  log "Added delegated permissions openid, profile, email"
fi
if az ad app permission admin-consent --id "$APP_ID" 2>/dev/null; then
  log "Admin consent granted for openid, profile, email"
else
  warn "Couldn't grant admin consent; you'll be asked to consent on first sign-in instead"
fi

ensure_app_secret "$APP_ID" rockbot-blazor BLAZOR_AUTH_MICROSOFT_CLIENT_SECRET BLAZOR_AUTH_MICROSOFT_CLIENT_ID "$rotate"

upsert_env BLAZOR_AUTH_ENABLED true
upsert_env BLAZOR_AUTH_PUBLIC_BASE_URL "https://$host"
upsert_env BLAZOR_AUTH_MICROSOFT_TENANT_ID "$tenant"
upsert_env BLAZOR_AUTH_MICROSOFT_CLIENT_ID "$APP_ID"
upsert_env BLAZOR_AUTH_ALLOWED_EMAILS "$allow_email"
log "Wrote BLAZOR_AUTH_* to $ENV_FILE (sign-in allowed for $allow_email)"
