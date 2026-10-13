# shellcheck shell=bash
# Shared helpers for the deploy/graph scripts. Source, don't execute.

GRAPH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$GRAPH_DIR/../.." && pwd)"
COMPOSE_DIR="$REPO_ROOT/deploy/docker-compose"
ENV_FILE="${ENV_FILE:-$COMPOSE_DIR/.env}"
# shellcheck disable=SC2034 # used by the scripts that source this file
DEFAULT_APP_NAME="RockBot Graph"
# shellcheck disable=SC2034
GRAPH_API_APP_ID="00000003-0000-0000-c000-000000000000"

log()  { printf '==> %s\n' "$*" >&2; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

require() {
  local cmd
  for cmd in "$@"; do
    command -v "$cmd" >/dev/null 2>&1 || die "'$cmd' is required but not installed"
  done
}

ensure_az_login() {
  if ! az account show >/dev/null 2>&1; then
    log "Not signed in to the Azure CLI; starting 'az login'"
    az login --allow-no-subscriptions >/dev/null
  fi
  log "Azure CLI tenant: $(az account show --query tenantId -o tsv) ($(az account show --query user.name -o tsv))"
}

# Prints the value of KEY from the compose .env file, or nothing.
env_get() {
  [[ -f "$ENV_FILE" ]] || return 0
  local line
  line="$(grep -E "^$1=" "$ENV_FILE" | tail -n 1 || true)"
  printf '%s' "${line#*=}"
}

# Sets KEY=VALUE in the compose .env file, replacing an existing line or appending.
# Values go through the environment, never argv, so secrets don't show up in `ps`.
upsert_env() {
  local key="$1" value="$2" tmp
  [[ -f "$ENV_FILE" ]] || (umask 077 && touch "$ENV_FILE")
  tmp="$(mktemp "$ENV_FILE.XXXXXX")"
  K="$key" V="$value" awk '
    BEGIN { k = ENVIRON["K"]; v = ENVIRON["V"] }
    index($0, k "=") == 1 { if (!done) print k "=" v; done = 1; next }
    { print }
    END { if (!done) print k "=" v }
  ' "$ENV_FILE" > "$tmp"
  chmod --reference="$ENV_FILE" "$tmp"
  mv "$tmp" "$ENV_FILE"
}

remove_env() {
  [[ -f "$ENV_FILE" ]] || return 0
  local tmp
  tmp="$(mktemp "$ENV_FILE.XXXXXX")"
  K="$1" awk 'index($0, ENVIRON["K"] "=") != 1' "$ENV_FILE" > "$tmp"
  chmod --reference="$ENV_FILE" "$tmp"
  mv "$tmp" "$ENV_FILE"
}

# Prints the appId of the app registration with exactly this display name, or nothing.
find_app_id() {
  local ids count
  ids="$(az ad app list --display-name "$1" --query "[?displayName=='$1'].appId" -o tsv)"
  count="$(grep -c . <<<"$ids" || true)"
  (( count <= 1 )) || die "found $count app registrations named '$1'; delete the extras or pass --app-name"
  printf '%s' "$ids"
}

require_value() {
  [[ -n "$2" ]] || die "$1 is not set; run 01-create-app.sh first (or check $ENV_FILE)"
}
