#!/usr/bin/env bash
# Azure DNS for the HTTPS front end. Safe to re-run.
#   - resource group + DNS zone (a subdomain you delegate from your registrar)
#   - A record HOST.ZONE -> this machine's LAN IP
#   - "RockBot Caddy DNS" app with DNS Zone Contributor on that resource group only, and a
#     secret, so Caddy can answer Let's Encrypt DNS-01 challenges
#   - writes RB_PUBLIC_HOST, RB_DNS_ZONE and CADDY_AZURE_* to deploy/docker-compose/.env
#   - checks the zone is delegated; if not, prints the NS records to add at your registrar
#     and exits 3
#
# Usage: 01-dns.sh --zone home.example.com [--host rockbot] [--ip auto] [--resource-group rockbot-dns]
#                  [--location australiaeast] [--rotate]
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

zone="$(env_get RB_DNS_ZONE)"
host_label=rockbot
ip=auto
rg="$(env_get CADDY_AZURE_RESOURCE_GROUP)"; rg="${rg:-rockbot-dns}"
location=australiaeast
rotate=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --zone) zone="$2"; shift 2 ;;
    --host) host_label="$2"; shift 2 ;;
    --ip) ip="$2"; shift 2 ;;
    --resource-group) rg="$2"; shift 2 ;;
    --location) location="$2"; shift 2 ;;
    --rotate) rotate=true; shift ;;
    -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done
[[ -n "$zone" ]] || die "--zone is required (a subdomain of a domain you own, e.g. home.example.com)"
[[ "$host_label" =~ ^[a-z0-9-]+$ ]] || die "--host must be a single DNS label"

require az jq dig
if [[ "$ip" == auto ]]; then
  ip="$(ip -4 route get 1.1.1.1 | sed -n 's/.* src \([0-9.]*\).*/\1/p')"
  [[ -n "$ip" ]] || die "could not detect the LAN IP; pass --ip"
fi
fqdn="$host_label.$zone"

ensure_az_login
subscription="$(az account show --query id -o tsv)"
tenant="$(az account show --query tenantId -o tsv)"
log "Subscription: $(az account show --query name -o tsv) ($subscription)"

if [[ "$(az group exists -n "$rg")" != true ]]; then
  az group create -n "$rg" -l "$location" -o none
  log "Created resource group $rg"
fi
if ! az network dns zone show -g "$rg" -n "$zone" -o none 2>/dev/null; then
  az network dns zone create -g "$rg" -n "$zone" -o none
  log "Created DNS zone $zone"
else
  log "Found DNS zone $zone"
fi

current_ips="$(az network dns record-set a show -g "$rg" -z "$zone" -n "$host_label" --query 'ARecords[].ipv4Address' -o tsv 2>/dev/null || true)"
if [[ "$current_ips" != "$ip" ]]; then
  [[ -z "$current_ips" ]] || az network dns record-set a delete -g "$rg" -z "$zone" -n "$host_label" --yes -o none
  az network dns record-set a add-record -g "$rg" -z "$zone" -n "$host_label" -a "$ip" --ttl 300 -o none
  log "Set $fqdn -> $ip"
else
  log "Found $fqdn -> $ip"
fi

ensure_app "$DNS_APP_NAME"
rg_id="$(az group show -n "$rg" --query id -o tsv)"
if [[ "$(az role assignment list --assignee "$SP_OBJECT_ID" --scope "$rg_id" --role "DNS Zone Contributor" --query 'length(@)' -o tsv)" == 0 ]]; then
  # A brand-new service principal can take a few seconds to be visible to Azure RBAC.
  for attempt in 1 2 3 4 5 6; do
    if az role assignment create --assignee-object-id "$SP_OBJECT_ID" --assignee-principal-type ServicePrincipal \
         --role "DNS Zone Contributor" --scope "$rg_id" -o none 2>/dev/null; then
      log "Granted DNS Zone Contributor on $rg"
      break
    fi
    (( attempt < 6 )) || die "could not create the role assignment on $rg"
    sleep 10
  done
else
  log "Found DNS Zone Contributor role on $rg"
fi
ensure_app_secret "$APP_ID" rockbot-caddy CADDY_AZURE_CLIENT_SECRET CADDY_AZURE_CLIENT_ID "$rotate"

upsert_env RB_DNS_ZONE "$zone"
upsert_env RB_PUBLIC_HOST "$fqdn"
upsert_env CADDY_AZURE_SUBSCRIPTION_ID "$subscription"
upsert_env CADDY_AZURE_RESOURCE_GROUP "$rg"
upsert_env CADDY_AZURE_TENANT_ID "$tenant"
upsert_env CADDY_AZURE_CLIENT_ID "$APP_ID"
log "Wrote RB_PUBLIC_HOST, RB_DNS_ZONE and CADDY_AZURE_* to $ENV_FILE"

azure_ns="$(az network dns zone show -g "$rg" -n "$zone" --query nameServers -o tsv | normalize_ns)"
public_ns="$(dig +short NS "$zone" @1.1.1.1 | normalize_ns || true)"
if [[ "$public_ns" != "$azure_ns" ]]; then
  parent="${zone#*.}"
  label="${zone%%."$parent"}"
  warn "$zone is not delegated to Azure DNS yet. At the registrar for $parent, add these NS records"
  warn "for the host '$label' (TTL 3600), then re-run this script:"
  while read -r ns; do warn "    $label  NS  $ns."; done <<<"$azure_ns"
  exit "$EXIT_NOT_DELEGATED"
fi
log "PASS: $zone is delegated to Azure DNS"

resolved="$(dig +short A "$fqdn" @1.1.1.1 | tail -n 1)"
if [[ "$resolved" == "$ip" ]]; then
  log "PASS: $fqdn resolves to $ip publicly"
else
  warn "$fqdn resolves to '${resolved:-nothing}' publicly, not $ip yet (DNS caches; try again in a few minutes)"
fi

if [[ "$(dig +short A "$fqdn" | tail -n 1)" != "$ip" ]]; then
  warn "Your LAN resolver doesn't return $ip for $fqdn. If that persists, your router is probably"
  warn "blocking public names that point at private IPs (DNS-rebinding protection): allow-list"
  warn "$zone there, or add a local DNS override for $fqdn -> $ip."
fi
