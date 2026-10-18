#!/usr/bin/env bash
# Connects RockBot to an Obsidian vault through Obsidian Sync. Safe to re-run.
#   - builds obsidian-sync (Obsidian's headless Sync client) and obsidian-mcp
#   - signs in to your Obsidian account and binds the obsidian-vault volume to one remote vault
#     (interactive the first time: email, password, MFA code, and the vault's encryption password)
#   - stores OBSIDIAN_* in deploy/docker-compose/.env, starts both services, waits for the first
#     sync and registers "obsidian" in the agent's mcp.json
#
# Usage: setup.sh [--vault "Remote vault name or id"] [--vault-name NAME] [--write-folder FOLDER]
#   --vault         remote vault to sync; prompted for (from a list) when not yet set up
#   --vault-name    the vault's name in Obsidian, for obsidian:// links (defaults to --vault)
#   --write-folder  the only folder RockBot may write to (default: RockBot, or the current setting)
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"

vault=""
vault_name=""
write_folder=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --vault) vault="$2"; shift 2 ;;
    --vault-name) vault_name="$2"; shift 2 ;;
    --write-folder) write_folder="$2"; shift 2 ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

require docker jq

write_folder="${write_folder:-$(env_get OBSIDIAN_WRITE_FOLDER)}"
write_folder="${write_folder:-RockBot}"
[[ "$write_folder" != /* && "$write_folder" != *..* && "$write_folder" != .* ]] \
  || die "--write-folder must be a relative folder inside the vault, e.g. RockBot"

C=("${COMPOSE[@]}" --profile obsidian)

# Runs ob in a throwaway obsidian-sync container: ob_tty for prompts, ob_batch for output we parse.
ob_tty()   { "${C[@]}" run --rm -it obsidian-sync ob "$@"; }
ob_batch() { "${C[@]}" run --rm -T obsidian-sync ob "$@"; }

log "Building obsidian-sync and obsidian-mcp"
"${C[@]}" build obsidian-sync obsidian-mcp

if ! ob_batch sync-list-remote >/dev/null 2>&1; then
  [[ -t 0 ]] || die "not signed in to Obsidian and there is no terminal to sign in on; run this script interactively"
  log "Sign in to your Obsidian account (the one with the Sync subscription)"
  ob_tty login
  ob_batch sync-list-remote >/dev/null 2>&1 || die "sign-in failed"
fi
log "Signed in to Obsidian"

if ob_batch sync-status --path /vault >/dev/null 2>&1; then
  log "The vault volume is already bound to a remote vault:"
  ob_batch sync-status --path /vault >&2 || true
  [[ -z "$vault" ]] || warn "--vault ignored; run teardown.sh --purge first to switch vaults"
else
  if [[ -z "$vault" ]]; then
    [[ -t 0 ]] || die "pass --vault when not running interactively"
    log "Your remote vaults:"
    ob_batch sync-list-remote >&2
    read -r -p "Remote vault name or id to give RockBot: " vault
    [[ -n "$vault" ]] || die "no vault chosen"
  fi
  log "Binding the vault volume to '$vault'. If the vault is end-to-end encrypted you'll be asked for its password."
  # Its own device name keeps RockBot's edits identifiable in Sync's version history.
  ob_tty sync-setup --vault "$vault" --path /vault --device-name RockBot
fi

vault_name="${vault_name:-$(env_get OBSIDIAN_VAULT_NAME)}"
vault_name="${vault_name:-$vault}"
upsert_env OBSIDIAN_WRITE_FOLDER "$write_folder"
upsert_env OBSIDIAN_VAULT_NAME "$vault_name"
log "Stored OBSIDIAN_WRITE_FOLDER=$write_folder and OBSIDIAN_VAULT_NAME=$vault_name in $ENV_FILE"

enable_profile obsidian
log "Starting obsidian-sync and obsidian-mcp"
"${C[@]}" up -d obsidian-sync obsidian-mcp

log "Waiting for the first sync to bring notes down (up to 5 minutes)"
synced=false
for _ in $(seq 60); do
  if [[ -n "$("${C[@]}" exec -T obsidian-sync sh -c "find /vault -name '*.md' -not -path '*/.*' | head -n 1" 2>/dev/null)" ]]; then
    synced=true
    break
  fi
  sleep 5
done
if $synced; then
  log "PASS: $("${C[@]}" exec -T obsidian-sync sh -c "find /vault -name '*.md' -not -path '*/.*' | wc -l" | tr -d ' ') notes in the vault volume"
else
  warn "No notes yet. The vault may be empty or still syncing: check  docker compose logs obsidian-sync"
fi

register_mcp_server obsidian '{"type":"sse","url":"http://obsidian-mcp:8080/"}'
log "Done. RockBot can read the whole vault and write under '$write_folder/'."
