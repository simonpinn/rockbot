#!/usr/bin/env bash
# Turns on the graph-mcp compose service and registers it with the agent as the
# "graph-mail" MCP server. Safe to re-run.
#   - adds "graph" to COMPOSE_PROFILES in deploy/docker-compose/.env
#   - starts graph-mcp
#   - adds graph-mail to the agent's mcp.json if the agent is running (the agent
#     picks the change up without a restart)
#
# Usage: 04-enable-compose.sh
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[[ "${1:-}" != "-h" && "${1:-}" != "--help" ]] || { sed -n '2,10p' "$0"; exit 0; }

require docker jq
compose=(docker compose --project-directory "$COMPOSE_DIR" -f "$COMPOSE_DIR/docker-compose.yml")

profiles="$(env_get COMPOSE_PROFILES)"
if [[ ",$profiles," != *",graph,"* ]]; then
  upsert_env COMPOSE_PROFILES "${profiles:+$profiles,}graph"
  log "Added 'graph' to COMPOSE_PROFILES in $ENV_FILE"
else
  log "COMPOSE_PROFILES already includes 'graph'"
fi

log "Building and starting graph-mcp"
"${compose[@]}" up -d --build graph-mcp

if [[ -z "$("${compose[@]}" ps -q --status running agent)" ]]; then
  log "The agent isn't running. Start the stack, then re-run this script to register graph-mail:"
  log "  cd deploy/docker-compose && docker compose up -d"
  exit 0
fi

current="$("${compose[@]}" exec -T agent sh -c 'cat /data/agent/mcp.json 2>/dev/null || echo "{}"')"
entry='{"type":"sse","url":"http://graph-mcp:8080/"}'
if jq -e --argjson e "$entry" '.mcpServers["graph-mail"] == $e' <<<"$current" >/dev/null; then
  log "graph-mail is already registered in the agent's mcp.json"
  exit 0
fi

jq --argjson e "$entry" '.mcpServers["graph-mail"] = $e' <<<"$current" \
  | "${compose[@]}" exec -T agent sh -c 'cat > /data/agent/mcp.json.tmp && mv /data/agent/mcp.json.tmp /data/agent/mcp.json'
log "Registered graph-mail in the agent's mcp.json"
