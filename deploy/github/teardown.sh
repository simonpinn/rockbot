#!/usr/bin/env bash
# Undoes setup.sh: unregisters "github" from the agent and removes GITHUB_MCP_TOKEN.
# Revoke the token itself at https://github.com/settings/personal-access-tokens. Safe to re-run.
#
# Usage: teardown.sh
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"

[[ "${1:-}" != "-h" && "${1:-}" != "--help" ]] || { sed -n '2,5p' "$0"; exit 0; }

require docker jq
unregister_mcp_server github
remove_env GITHUB_MCP_TOKEN
log "Removed GITHUB_MCP_TOKEN from $ENV_FILE"
if agent_running; then
  log "Recreating the agent so it drops GITHUB_MCP_TOKEN"
  "${COMPOSE[@]}" up -d agent
fi
log "Now revoke the token at https://github.com/settings/personal-access-tokens"
