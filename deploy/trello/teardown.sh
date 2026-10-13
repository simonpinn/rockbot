#!/usr/bin/env bash
# Undoes setup.sh: unregisters "trello" from the agent, removes the trello-mcp container
# and the TRELLO_* settings. Revoke the token itself in Trello (Settings > Applications).
# Safe to re-run.
#
# Usage: teardown.sh
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"

[[ "${1:-}" != "-h" && "${1:-}" != "--help" ]] || { sed -n '2,6p' "$0"; exit 0; }

require docker jq
unregister_mcp_server trello
"${COMPOSE[@]}" --profile trello rm -sf trello-mcp >/dev/null 2>&1 || true
for key in TRELLO_API_KEY TRELLO_TOKEN TRELLO_ALLOWED_BOARD_IDS; do
  remove_env "$key"
done
disable_profile trello
log "Removed TRELLO_* settings from $ENV_FILE"
log "Now revoke the RockBot token in Trello: Settings > Applications"
