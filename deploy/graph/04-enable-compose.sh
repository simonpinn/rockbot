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
enable_profile graph

log "Building and starting graph-mcp"
"${COMPOSE[@]}" up -d --build graph-mcp

register_mcp_server graph-mail '{"type":"sse","url":"http://graph-mcp:8080/"}'
