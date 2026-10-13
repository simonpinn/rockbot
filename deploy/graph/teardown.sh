#!/usr/bin/env bash
# Undoes setup.sh: removes the Exchange scoping, deletes the Entra app registration,
# unregisters graph-mail from the agent, and removes the GRAPH_* settings from .env.
# Safe to re-run.
#
# Usage: teardown.sh [--yes]
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

assume_yes=false
case "${1:-}" in
  --yes) assume_yes=true ;;
  -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
  "") ;;
  *) die "unknown argument: $1" ;;
esac

require az docker jq
app_id="$(env_get GRAPH_CLIENT_ID)"
app_name="$(env_get GRAPH_APP_NAME)"
app_name="${app_name:-$DEFAULT_APP_NAME}"

if ! $assume_yes; then
  read -r -p "Delete app registration '$app_name' (${app_id:-not in .env}) and its Exchange grant? [y/N] " answer
  [[ "$answer" == [yY]* ]] || die "aborted"
fi

compose=(docker compose --project-directory "$COMPOSE_DIR" -f "$COMPOSE_DIR/docker-compose.yml")
if [[ -n "$("${compose[@]}" ps -q --status running agent 2>/dev/null)" ]]; then
  "${compose[@]}" exec -T agent sh -c 'cat /data/agent/mcp.json 2>/dev/null || echo "{}"' \
    | jq 'del(.mcpServers["graph-mail"])' \
    | "${compose[@]}" exec -T agent sh -c 'cat > /data/agent/mcp.json.tmp && mv /data/agent/mcp.json.tmp /data/agent/mcp.json'
  log "Unregistered graph-mail from the agent"
fi
"${compose[@]}" --profile graph rm -sf graph-mcp >/dev/null 2>&1 || true

if [[ -n "$app_id" ]]; then
  tty_flags=(-i)
  [[ -t 1 ]] && tty_flags+=(-t)
  docker run --rm "${tty_flags[@]}" \
    -e ACTION=remove \
    -e APP_ID="$app_id" \
    -v rockbot-pwsh-modules:/root/.local/share/powershell/Modules \
    -v "$GRAPH_DIR/scope-mailbox.ps1:/work/scope-mailbox.ps1:ro" \
    "${PWSH_IMAGE:-mcr.microsoft.com/powershell:lts}" \
    pwsh -NoProfile -File /work/scope-mailbox.ps1
fi

ensure_az_login
existing="$(find_app_id "$app_name")"
if [[ -n "$existing" ]]; then
  az ad app delete --id "$existing"
  log "Deleted app registration '$app_name' ($existing)"
else
  log "No app registration named '$app_name'"
fi

for key in GRAPH_TENANT_ID GRAPH_CLIENT_ID GRAPH_CLIENT_SECRET GRAPH_SP_OBJECT_ID GRAPH_MAILBOX GRAPH_APP_NAME; do
  remove_env "$key"
done
profiles="$(env_get COMPOSE_PROFILES)"
if [[ -n "$profiles" ]]; then
  remaining="$(tr ',' '\n' <<<"$profiles" | grep -vx graph | paste -sd, - || true)"
  if [[ -n "$remaining" ]]; then upsert_env COMPOSE_PROFILES "$remaining"; else remove_env COMPOSE_PROFILES; fi
fi
log "Removed GRAPH_* settings from $ENV_FILE"
