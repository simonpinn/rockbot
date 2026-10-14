# shellcheck shell=bash
# Azure CLI helpers shared by the deploy scripts. Source after common.sh.

ensure_az_login() {
  if ! az account show >/dev/null 2>&1; then
    log "Not signed in to the Azure CLI; starting 'az login'"
    az login --allow-no-subscriptions >/dev/null
  fi
  log "Azure CLI tenant: $(az account show --query tenantId -o tsv) ($(az account show --query user.name -o tsv))"
}

# Prints the appId of the app registration with exactly this display name, or nothing.
find_app_id() {
  local ids count
  ids="$(az ad app list --display-name "$1" --query "[?displayName=='$1'].appId" -o tsv)"
  count="$(grep -c . <<<"$ids" || true)"
  (( count <= 1 )) || die "found $count app registrations named '$1'; delete the extras or pass a different name"
  printf '%s' "$ids"
}

# Finds or creates the app registration NAME (extra `az ad app create` args after it) and its
# service principal. Sets APP_ID and SP_OBJECT_ID.
ensure_app() {
  local name="$1"; shift
  [[ "$name" != *"'"* ]] || die "app name must not contain a single quote"
  APP_ID="$(find_app_id "$name")"
  if [[ -z "$APP_ID" ]]; then
    APP_ID="$(az ad app create --display-name "$name" --sign-in-audience AzureADMyOrg "$@" --query appId -o tsv)"
    log "Created app registration '$name' ($APP_ID)"
  else
    log "Found app registration '$name' ($APP_ID)"
  fi
  SP_OBJECT_ID="$(az ad sp show --id "$APP_ID" --query id -o tsv 2>/dev/null || true)"
  if [[ -z "$SP_OBJECT_ID" ]]; then
    SP_OBJECT_ID="$(az ad sp create --id "$APP_ID" --query id -o tsv)"
    log "Created service principal ($SP_OBJECT_ID)"
  fi
}

# Mints a client secret named SECRET_NAME on APP_ID and stores it in .env as ENV_KEY when there is
# none stored, the stored one belongs to another app (ID_ENV_KEY differs), or ROTATE is true.
# Older secrets with the same name are deleted afterwards.
ensure_app_secret() {
  local app_id="$1" secret_name="$2" env_key="$3" id_env_key="$4" rotate="$5" years="${6:-1}" old secret key_id
  if [[ "$rotate" != true && -n "$(env_get "$env_key")" && "$(env_get "$id_env_key")" == "$app_id" ]]; then
    log "Client secret already present in $ENV_FILE (use --rotate to replace it)"
    return 0
  fi
  old="$(az ad app credential list --id "$app_id" --query "[?displayName=='$secret_name'].keyId" -o tsv)"
  secret="$(az ad app credential reset --id "$app_id" --append --display-name "$secret_name" \
    --years "$years" --query password -o tsv 2>/dev/null)"
  [[ -n "$secret" ]] || die "failed to create a client secret"
  upsert_env "$env_key" "$secret"
  log "Created client secret '$secret_name' (expires in $years year(s)); stored as $env_key in $ENV_FILE"
  for key_id in $old; do
    az ad app credential delete --id "$app_id" --key-id "$key_id" >/dev/null
    log "Removed previous '$secret_name' secret ($key_id)"
  done
}
