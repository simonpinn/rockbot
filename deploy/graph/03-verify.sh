#!/usr/bin/env bash
# Checks the scoping end to end with the app's own credentials:
#   - the configured mailbox's mail, calendar and contacts are readable (HTTP 200)
#   - another user's mail, calendar and contacts are NOT readable (HTTP 403), when --other-user is given
#   - the app has no tenant-wide Graph application permissions in Entra
# Exits non-zero if any check fails. Safe to re-run.
#
# Usage: 03-verify.sh [--other-user someone@domain]
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

other_user=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --other-user) other_user="$2"; shift 2 ;;
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

require az jq curl
tenant_id="$(env_get GRAPH_TENANT_ID)"
client_id="$(env_get GRAPH_CLIENT_ID)"
sp_object_id="$(env_get GRAPH_SP_OBJECT_ID)"
mailbox="$(env_get GRAPH_MAILBOX)"
require_value GRAPH_TENANT_ID "$tenant_id"
require_value GRAPH_CLIENT_ID "$client_id"
require_value GRAPH_MAILBOX "$mailbox"
[[ -n "$(env_get GRAPH_CLIENT_SECRET)" ]] || die "GRAPH_CLIENT_SECRET is not set; run 01-create-app.sh"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
chmod 700 "$work"
failed=0

# 1. Token via client credentials. The secret goes to curl on stdin, the token via a
#    header file, so neither appears in the process list. A brand-new secret can take
#    a minute to become usable, hence the retries.
token=""
for attempt in 1 2 3 4 5 6; do
  if env_get GRAPH_CLIENT_SECRET | curl -sS -o "$work/token.json" -w '%{http_code}' \
       "https://login.microsoftonline.com/$tenant_id/oauth2/v2.0/token" \
       --data-urlencode "client_id=$client_id" \
       --data-urlencode "client_secret@-" \
       --data-urlencode "scope=https://graph.microsoft.com/.default" \
       --data-urlencode "grant_type=client_credentials" > "$work/token.status" \
     && [[ "$(cat "$work/token.status")" == 200 ]]; then
    token="$(jq -r .access_token "$work/token.json")"
    break
  fi
  log "Token request failed (attempt $attempt): $(jq -r '.error_description // .error // "unknown error"' "$work/token.json" 2>/dev/null | head -n 1)"
  sleep 10
done
[[ -n "$token" ]] || die "could not get a Graph token; check the GRAPH_* values in $ENV_FILE (or run 01-create-app.sh --rotate)"
printf 'Authorization: Bearer %s\n' "$token" > "$work/auth.header"
unset token
log "PASS: obtained an app-only Graph token"

# Prints the HTTP status of reading one item of a kind (messages, calendar/events,
# contacts) from the given mailbox.
item_status() {
  local upn
  upn="$(jq -rn --arg v "$1" '$v|@uri')"
  curl -sS -o "$work/graph.json" -w '%{http_code}' -H "@$work/auth.header" \
    "https://graph.microsoft.com/v1.0/users/$upn/$2?\$top=1&\$select=id"
}

graph_error() { jq -r '.error.code + ": " + .error.message' "$work/graph.json" 2>/dev/null || true; }

kinds=(messages calendar/events contacts)

# 2. Own mailbox must be readable, for every kind.
for kind in "${kinds[@]}"; do
  status="$(item_status "$mailbox" "$kind")"
  case "$status" in
    200) log "PASS: $kind in $mailbox is readable" ;;
    403) warn "$kind in $mailbox returned 403 ($(graph_error))."
         warn "If 02-scope-mailbox.sh ran recently, Exchange can take 30 minutes to 2 hours to apply it; re-run this script later."
         failed=1 ;;
    *)   warn "$kind in $mailbox returned $status ($(graph_error))"; failed=1 ;;
  esac
done

# 3. Another mailbox must NOT be readable, for every kind.
if [[ -n "$other_user" ]]; then
  for kind in "${kinds[@]}"; do
    status="$(item_status "$other_user" "$kind")"
    case "$status" in
      403) log "PASS: $kind in $other_user is denied (403), so access is limited to $mailbox" ;;
      200) warn "FAIL: $kind in $other_user is readable. The app has tenant-wide access; see the Entra check below."; failed=1 ;;
      404) warn "$other_user returned 404 for $kind (no such user or no mailbox); pick a user with a mailbox to prove the scoping" ;;
      *)   warn "$kind in $other_user returned $status ($(graph_error))"; failed=1 ;;
    esac
  done
else
  log "SKIP: pass --other-user someone@domain to prove other mailboxes are denied"
fi

# 4. No Entra-level Graph application permissions: those would apply tenant-wide and
#    override the Exchange scoping.
if [[ -n "$sp_object_id" ]] && command -v az >/dev/null && az account show >/dev/null 2>&1; then
  graph_sp_id="$(az ad sp show --id "$GRAPH_API_APP_ID" --query id -o tsv)"
  grants="$(az rest --method GET \
    --url "https://graph.microsoft.com/v1.0/servicePrincipals/$sp_object_id/appRoleAssignments" \
    --query "value[?resourceId=='$graph_sp_id'].appRoleId" -o tsv)"
  if [[ -n "$grants" ]]; then
    warn "FAIL: the app has Entra-level Microsoft Graph application permissions (appRoleIds: $(tr '\n' ' ' <<<"$grants"))."
    warn "These apply to the whole tenant. Remove them under Enterprise applications > $(env_get GRAPH_APP_NAME) > Permissions."
    failed=1
  else
    log "PASS: no tenant-wide Graph application permissions in Entra"
  fi
else
  log "SKIP: Entra permission check (not signed in to the Azure CLI)"
fi

if (( failed )); then
  die "verification failed"
fi
log "All checks passed"
