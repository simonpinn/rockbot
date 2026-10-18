#!/usr/bin/env bash
# Connects RockBot to GitHub's hosted MCP server (api.githubcopilot.com/mcp) in read-only
# mode, using a fine-grained personal access token. Safe to re-run.
#   - checks the token against the GitHub API and the MCP endpoint
#   - stores it as GITHUB_MCP_TOKEN in deploy/docker-compose/.env
#   - recreates the agent container if its copy of the token is out of date
#   - registers "github" in the agent's mcp.json (the token is referenced as
#     ${GITHUB_MCP_TOKEN}, never written into mcp.json)
#
# Usage: setup.sh [--new-token] [--toolsets repos,issues,pull_requests,actions]
#   The token is read from GITHUB_MCP_TOKEN, or prompted for. An existing token in
#   .env is reused unless --new-token is given.
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"

MCP_URL="https://api.githubcopilot.com/mcp/"
toolsets="repos,issues,pull_requests,actions"
new_token=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --new-token) new_token=true; shift ;;
    --toolsets) toolsets="$2"; shift 2 ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

require curl jq docker

token="${GITHUB_MCP_TOKEN:-}"
if [[ -z "$token" ]] && ! $new_token; then
  token="$(env_get GITHUB_MCP_TOKEN)"
fi
if [[ -z "$token" ]]; then
  log "Create a fine-grained token at https://github.com/settings/personal-access-tokens/new"
  log "(read-only: Contents, Issues, Pull requests, Actions, Metadata; see deploy/github/README.md)"
  token="$(read_secret GITHUB_MCP_TOKEN "Paste the GitHub token")"
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
chmod 700 "$work"
printf 'Authorization: Bearer %s\n' "$token" > "$work/auth.header"

[[ "$token" == github_pat_* ]] \
  || warn "This isn't a fine-grained token (github_pat_...). Classic and OAuth tokens usually grant far more than RockBot needs."

status="$(curl -sS -o "$work/user.json" -w '%{http_code}' -H "@$work/auth.header" https://api.github.com/user)"
[[ "$status" == 200 ]] || die "GitHub rejected the token (HTTP $status): $(jq -r '.message // empty' "$work/user.json" 2>/dev/null)"
log "PASS: token belongs to $(jq -r .login "$work/user.json")"

status="$(curl -sS -o "$work/init.json" -w '%{http_code}' -H "@$work/auth.header" \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -H 'X-MCP-Readonly: true' -H "X-MCP-Toolsets: $toolsets" \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"rockbot-setup","version":"1"}}}' \
  "$MCP_URL")"
[[ "$status" == 200 ]] || die "The GitHub MCP server rejected the token (HTTP $status): $(head -c 300 "$work/init.json")"
log "PASS: GitHub MCP server accepted the token"

if [[ "$(env_get GITHUB_MCP_TOKEN)" != "$token" ]]; then
  upsert_env GITHUB_MCP_TOKEN "$token"
  log "Stored GITHUB_MCP_TOKEN in $ENV_FILE"
fi

if agent_running && [[ "$("${COMPOSE[@]}" exec -T agent printenv GITHUB_MCP_TOKEN 2>/dev/null || true)" != "$token" ]]; then
  log "Recreating the agent so it picks up the new GITHUB_MCP_TOKEN"
  "${COMPOSE[@]}" up -d agent
fi
unset token

entry="$(jq -n --arg url "$MCP_URL" --arg toolsets "$toolsets" '{
  type: "http",
  url: $url,
  transportMode: "streamable-http",
  headers: {
    "Authorization": "Bearer ${GITHUB_MCP_TOKEN}",
    "X-MCP-Toolsets": $toolsets,
    "X-MCP-Readonly": "true"
  }
}')"
register_mcp_server github "$entry"
