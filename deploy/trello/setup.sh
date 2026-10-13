#!/usr/bin/env bash
# Connects RockBot to Trello through the trello-mcp container. Safe to re-run.
#   - checks the API key and token against the Trello API
#   - optionally limits RockBot to named boards (--boards)
#   - stores TRELLO_* in deploy/docker-compose/.env
#   - starts trello-mcp and registers "trello" in the agent's mcp.json
#
# Usage: setup.sh [--new-token] [--boards "Board name or id,..." | --all-boards]
#   The key and token are read from TRELLO_API_KEY / TRELLO_TOKEN, or prompted for.
#   Existing values in .env are reused unless --new-token is given. Without --boards
#   or --all-boards the current board allowlist is kept.
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"

new_token=false
boards=""
all_boards=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --new-token) new_token=true; shift ;;
    --boards) boards="$2"; shift 2 ;;
    --all-boards) all_boards=true; shift ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done
[[ -z "$boards" ]] || ! $all_boards || die "pass either --boards or --all-boards, not both"

require curl jq docker

api_key="${TRELLO_API_KEY:-}"
token="${TRELLO_TOKEN:-}"
if ! $new_token; then
  api_key="${api_key:-$(env_get TRELLO_API_KEY)}"
  token="${token:-$(env_get TRELLO_TOKEN)}"
fi
if [[ -z "$api_key" ]]; then
  log "Create a Power-Up at https://trello.com/power-ups/admin and generate its API key (see deploy/trello/README.md)"
  api_key="$(read_secret TRELLO_API_KEY "Paste the Trello API key")"
fi
if [[ -z "$token" ]]; then
  log "Open this URL, approve access, and copy the token it shows:"
  log "  https://trello.com/1/authorize?expiration=never&scope=read,write&response_type=token&name=RockBot&key=$api_key"
  token="$(read_secret TRELLO_TOKEN "Paste the Trello token")"
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
chmod 700 "$work"
printf 'Authorization: OAuth oauth_consumer_key="%s", oauth_token="%s"\n' "$api_key" "$token" > "$work/auth.header"

trello_get() {
  curl -sS -o "$work/out.json" -w '%{http_code}' -H "@$work/auth.header" "https://api.trello.com/1/$1"
}

status="$(trello_get 'members/me?fields=username,fullName')"
[[ "$status" == 200 ]] || die "Trello rejected the key or token (HTTP $status): $(head -c 200 "$work/out.json")"
log "PASS: token belongs to $(jq -r '.fullName + " (@" + .username + ")"' "$work/out.json")"

status="$(trello_get 'members/me/boards?filter=open&fields=name')"
[[ "$status" == 200 ]] || die "could not list boards (HTTP $status)"
cp "$work/out.json" "$work/boards.json"

allowed="$(env_get TRELLO_ALLOWED_BOARD_IDS)"
if $all_boards; then
  allowed=""
elif [[ -n "$boards" ]]; then
  ids=()
  IFS=',' read -r -a wanted <<<"$boards"
  for item in "${wanted[@]}"; do
    item="$(sed 's/^ *//; s/ *$//' <<<"$item")"
    [[ -n "$item" ]] || continue
    matches="$(jq -r --arg w "$item" '[.[] | select(.id == $w or (.name | ascii_downcase) == ($w | ascii_downcase)) | .id] | unique | .[]' "$work/boards.json")"
    case "$(grep -c . <<<"$matches" || true)" in
      1) ids+=("$matches") ;;
      0) die "no open board named or with id '$item'" ;;
      *) die "more than one board is named '$item'; pass its id instead" ;;
    esac
  done
  allowed="$(IFS=,; echo "${ids[*]}")"
fi

if [[ -n "$allowed" ]]; then
  log "RockBot may use these boards:"
  jq -r --arg ids "$allowed" '($ids | split(",")) as $a | .[] | select(.id as $i | $a | index($i)) | "    \(.name) (\(.id))"' "$work/boards.json" >&2
else
  warn "No board allowlist: RockBot can use all $(jq length "$work/boards.json") open boards this token can see. Limit it with --boards."
fi

upsert_env TRELLO_API_KEY "$api_key"
upsert_env TRELLO_TOKEN "$token"
upsert_env TRELLO_ALLOWED_BOARD_IDS "$allowed"
unset api_key token
log "Stored TRELLO_API_KEY, TRELLO_TOKEN and TRELLO_ALLOWED_BOARD_IDS in $ENV_FILE"

enable_profile trello
log "Building and starting trello-mcp"
"${COMPOSE[@]}" up -d --build trello-mcp

register_mcp_server trello '{"type":"sse","url":"http://trello-mcp:8080/"}'
