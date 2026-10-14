# shellcheck shell=bash
# Helpers for the deploy/https scripts. Source, don't execute.

HTTPS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../lib/common.sh
source "$HTTPS_DIR/../lib/common.sh"
# shellcheck source=../lib/azure.sh
source "$HTTPS_DIR/../lib/azure.sh"

# shellcheck disable=SC2034 # used by the scripts that source this file
DNS_APP_NAME="RockBot Caddy DNS"
# shellcheck disable=SC2034
SIGNIN_APP_NAME="RockBot Sign-in"
# Exit code 01-dns.sh uses when the zone isn't delegated yet, so setup.sh can stop cleanly.
# shellcheck disable=SC2034
EXIT_NOT_DELEGATED=3

# Lowercased, trailing-dot-free, sorted name servers, one per line.
normalize_ns() { tr '[:upper:]' '[:lower:]' | sed 's/\.$//' | grep . | sort -u; }
