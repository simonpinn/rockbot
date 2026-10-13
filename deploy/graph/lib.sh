# shellcheck shell=bash
# Graph-specific helpers for the deploy/graph scripts. Source, don't execute.

GRAPH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../lib/common.sh
source "$GRAPH_DIR/../lib/common.sh"

# shellcheck disable=SC2034 # used by the scripts that source this file
DEFAULT_APP_NAME="RockBot Graph"
# shellcheck disable=SC2034
GRAPH_API_APP_ID="00000003-0000-0000-c000-000000000000"

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
  (( count <= 1 )) || die "found $count app registrations named '$1'; delete the extras or pass --app-name"
  printf '%s' "$ids"
}

require_value() {
  [[ -n "$2" ]] || die "$1 is not set; run 01-create-app.sh first (or check $ENV_FILE)"
}
