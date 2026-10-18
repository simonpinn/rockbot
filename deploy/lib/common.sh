# shellcheck shell=bash
# Shared helpers for the deploy/{graph,github,trello} setup scripts. Source, don't execute.

DEPLOY_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$DEPLOY_LIB_DIR/../.." && pwd)"
COMPOSE_DIR="$REPO_ROOT/deploy/docker-compose"
ENV_FILE="${ENV_FILE:-$COMPOSE_DIR/.env}"
# shellcheck disable=SC2034 # used by the scripts that source this file
COMPOSE=(docker compose --project-directory "$COMPOSE_DIR" -f "$COMPOSE_DIR/docker-compose.yml")

log()  { printf '==> %s\n' "$*" >&2; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

require() {
  local cmd
  for cmd in "$@"; do
    command -v "$cmd" >/dev/null 2>&1 || die "'$cmd' is required but not installed"
  done
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

# Reads a secret from VAR if set, otherwise prompts for it without echo.
read_secret() {
  local var="$1" prompt="$2" value="${!1:-}"
  if [[ -z "$value" ]]; then
    [[ -t 0 ]] || die "$var is not set and there is no terminal to prompt on"
    read -r -s -p "$prompt: " value
    printf '\n' >&2
  fi
  [[ -n "$value" ]] || die "$var must not be empty"
  printf '%s' "$value"
}

enable_profile() {
  local profiles
  profiles="$(env_get COMPOSE_PROFILES)"
  if [[ ",$profiles," != *",$1,"* ]]; then
    upsert_env COMPOSE_PROFILES "${profiles:+$profiles,}$1"
    log "Added '$1' to COMPOSE_PROFILES in $ENV_FILE"
  fi
}

disable_profile() {
  local profiles remaining
  profiles="$(env_get COMPOSE_PROFILES)"
  [[ -n "$profiles" ]] || return 0
  remaining="$(tr ',' '\n' <<<"$profiles" | grep -vx "$1" | paste -sd, - || true)"
  if [[ -n "$remaining" ]]; then upsert_env COMPOSE_PROFILES "$remaining"; else remove_env COMPOSE_PROFILES; fi
}

agent_running() {
  [[ -n "$("${COMPOSE[@]}" ps -q --status running agent 2>/dev/null)" ]]
}

_write_mcp_json() {
  "${COMPOSE[@]}" exec -T agent sh -c 'cat > /data/agent/mcp.json.tmp && mv /data/agent/mcp.json.tmp /data/agent/mcp.json'
}

_read_mcp_json() {
  "${COMPOSE[@]}" exec -T agent sh -c 'cat /data/agent/mcp.json 2>/dev/null || echo "{}"'
}

# Adds or updates NAME in the agent's mcp.json with the fields in ENTRY_JSON, keeping any
# fields the bridge added itself (such as its stable id). No-op when already up to date.
register_mcp_server() {
  local name="$1" entry="$2" current
  if ! agent_running; then
    log "The agent isn't running, so $name can't be registered yet. Start the stack and re-run:"
    log "  cd deploy/docker-compose && docker compose up -d"
    return 0
  fi
  current="$(_read_mcp_json)"
  if jq -e --arg n "$name" --argjson e "$entry" \
       '(.mcpServers[$n] // {}) as $c | ($c | with_entries(select(.key as $k | $e | has($k)))) == $e' \
       <<<"$current" >/dev/null; then
    log "$name is already registered in the agent's mcp.json"
    return 0
  fi
  jq --arg n "$name" --argjson e "$entry" '.mcpServers[$n] = ((.mcpServers[$n] // {}) + $e)' <<<"$current" | _write_mcp_json
  log "Registered $name in the agent's mcp.json"
}

unregister_mcp_server() {
  agent_running || return 0
  _read_mcp_json | jq --arg n "$1" 'del(.mcpServers[$n])' | _write_mcp_json
  log "Unregistered $1 from the agent"
}
