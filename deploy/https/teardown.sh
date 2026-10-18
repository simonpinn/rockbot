#!/usr/bin/env bash
# Undoes setup.sh: stops caddy, turns sign-in off, deletes the two app registrations and
# removes the settings from .env. The DNS zone is kept unless --delete-zone is given (deleting
# it breaks the NS delegation at your registrar). Safe to re-run.
#
# Usage: teardown.sh [--delete-zone] [--yes]
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

delete_zone=false
assume_yes=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --delete-zone) delete_zone=true; shift ;;
    --yes) assume_yes=true; shift ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

require az docker
if ! $assume_yes; then
  read -r -p "Remove HTTPS access, sign-in and the '$DNS_APP_NAME' / '$SIGNIN_APP_NAME' apps? [y/N] " answer
  [[ "$answer" == [yY]* ]] || die "aborted"
fi

rg="$(env_get CADDY_AZURE_RESOURCE_GROUP)"
"${COMPOSE[@]}" --profile https rm -sf caddy >/dev/null 2>&1 || true
disable_profile https

ensure_az_login
for name in "$SIGNIN_APP_NAME" "$DNS_APP_NAME"; do
  id="$(find_app_id "$name")"
  if [[ -n "$id" ]]; then
    az ad app delete --id "$id"
    log "Deleted app registration '$name'"
  fi
done
if $delete_zone && [[ -n "$rg" ]] && [[ "$(az group exists -n "$rg")" == true ]]; then
  az group delete -n "$rg" --yes
  log "Deleted resource group $rg (remove the NS records at your registrar too)"
fi

for key in RB_PUBLIC_HOST RB_DNS_ZONE CADDY_AZURE_SUBSCRIPTION_ID CADDY_AZURE_RESOURCE_GROUP CADDY_AZURE_TENANT_ID \
           CADDY_AZURE_CLIENT_ID CADDY_AZURE_CLIENT_SECRET BLAZOR_AUTH_PUBLIC_BASE_URL BLAZOR_AUTH_MICROSOFT_TENANT_ID \
           BLAZOR_AUTH_MICROSOFT_CLIENT_ID BLAZOR_AUTH_MICROSOFT_CLIENT_SECRET BLAZOR_AUTH_ALLOWED_EMAILS; do
  remove_env "$key"
done
upsert_env BLAZOR_AUTH_ENABLED false
log "Removed the HTTPS and sign-in settings from $ENV_FILE"

"${COMPOSE[@]}" up -d blazor
log "Blazor is back to http://localhost:8080 with sign-in off"
