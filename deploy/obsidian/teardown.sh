#!/usr/bin/env bash
# Undoes setup.sh: unregisters "obsidian" from the agent, removes the obsidian-sync and
# obsidian-mcp containers and the OBSIDIAN_* settings. The vault copy and the Obsidian login
# are kept in their volumes, so setup.sh can bring it back quickly. Safe to re-run.
#
# Usage: teardown.sh [--purge]
#   --purge  also signs out of Obsidian and deletes the obsidian-vault and obsidian-config
#            volumes. The remote vault is untouched; only this machine's copy goes.
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"

purge=false
case "${1:-}" in
  "") ;;
  --purge) purge=true ;;
  -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
  *) die "unknown argument: $1" ;;
esac

require docker jq
C=("${COMPOSE[@]}" --profile obsidian)

unregister_mcp_server obsidian
"${C[@]}" rm -sf obsidian-sync obsidian-mcp >/dev/null 2>&1 || true

if $purge; then
  "${C[@]}" run --rm -T obsidian-sync ob sync-unlink --path /vault >/dev/null 2>&1 || true
  "${C[@]}" run --rm -T obsidian-sync ob logout >/dev/null 2>&1 || true
  project="$("${COMPOSE[@]}" config --format json | jq -r .name)"
  docker volume rm "${project}_obsidian-vault" "${project}_obsidian-config" >/dev/null 2>&1 || true
  log "Signed out of Obsidian and deleted the local vault copy"
fi

for key in OBSIDIAN_WRITE_FOLDER OBSIDIAN_VAULT_NAME; do
  remove_env "$key"
done
disable_profile obsidian
log "Removed OBSIDIAN_* settings from $ENV_FILE"
