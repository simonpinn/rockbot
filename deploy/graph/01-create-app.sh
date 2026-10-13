#!/usr/bin/env bash
# Creates (or finds) the Entra app registration + service principal and a client secret,
# and writes the GRAPH_* settings into deploy/docker-compose/.env. Safe to re-run.
#
# Usage: 01-create-app.sh --mailbox you@domain [--app-name NAME] [--rotate] [--secret-years N]
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

app_name="$DEFAULT_APP_NAME"
mailbox=""
rotate=false
secret_years=1
secret_name="rockbot-compose"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mailbox) mailbox="$2"; shift 2 ;;
    --app-name) app_name="$2"; shift 2 ;;
    --rotate) rotate=true; shift ;;
    --secret-years) secret_years="$2"; shift 2 ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

mailbox="${mailbox:-$(env_get GRAPH_MAILBOX)}"
[[ -n "$mailbox" ]] || die "--mailbox is required (the UPN / address RockBot may read)"
[[ "$app_name" != *"'"* ]] || die "--app-name must not contain a single quote"

require az jq
ensure_az_login
tenant_id="$(az account show --query tenantId -o tsv)"

app_id="$(find_app_id "$app_name")"
if [[ -z "$app_id" ]]; then
  app_id="$(az ad app create --display-name "$app_name" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
  log "Created app registration '$app_name' ($app_id)"
else
  log "Found app registration '$app_name' ($app_id)"
fi

sp_object_id="$(az ad sp show --id "$app_id" --query id -o tsv 2>/dev/null || true)"
if [[ -z "$sp_object_id" ]]; then
  sp_object_id="$(az ad sp create --id "$app_id" --query id -o tsv)"
  log "Created service principal ($sp_object_id)"
else
  log "Found service principal ($sp_object_id)"
fi

# A secret is only minted when needed: none stored yet, the stored one belongs to a
# different app (e.g. the app was deleted and recreated), or --rotate was asked for.
if $rotate || [[ -z "$(env_get GRAPH_CLIENT_SECRET)" ]] || [[ "$(env_get GRAPH_CLIENT_ID)" != "$app_id" ]]; then
  old_key_ids="$(az ad app credential list --id "$app_id" --query "[?displayName=='$secret_name'].keyId" -o tsv)"
  secret="$(az ad app credential reset --id "$app_id" --append --display-name "$secret_name" \
    --years "$secret_years" --query password -o tsv 2>/dev/null)"
  [[ -n "$secret" ]] || die "failed to create a client secret"
  upsert_env GRAPH_CLIENT_SECRET "$secret"
  unset secret
  log "Created client secret '$secret_name' (expires in $secret_years year(s)); stored in $ENV_FILE"
  for key_id in $old_key_ids; do
    az ad app credential delete --id "$app_id" --key-id "$key_id" >/dev/null
    log "Removed previous '$secret_name' secret ($key_id)"
  done
else
  log "Client secret already present in $ENV_FILE (use --rotate to replace it)"
fi

upsert_env GRAPH_TENANT_ID "$tenant_id"
upsert_env GRAPH_CLIENT_ID "$app_id"
upsert_env GRAPH_SP_OBJECT_ID "$sp_object_id"
upsert_env GRAPH_MAILBOX "$mailbox"
upsert_env GRAPH_APP_NAME "$app_name"
log "Wrote GRAPH_TENANT_ID, GRAPH_CLIENT_ID, GRAPH_SP_OBJECT_ID, GRAPH_MAILBOX, GRAPH_APP_NAME to $ENV_FILE"
