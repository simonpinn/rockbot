# shellcheck shell=bash
# Graph-specific helpers for the deploy/graph scripts. Source, don't execute.

GRAPH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../lib/common.sh
source "$GRAPH_DIR/../lib/common.sh"
# shellcheck source=../lib/azure.sh
source "$GRAPH_DIR/../lib/azure.sh"

# shellcheck disable=SC2034 # used by the scripts that source this file
DEFAULT_APP_NAME="RockBot Graph"
# shellcheck disable=SC2034
GRAPH_API_APP_ID="00000003-0000-0000-c000-000000000000"

require_value() {
  [[ -n "$2" ]] || die "$1 is not set; run 01-create-app.sh first (or check $ENV_FILE)"
}
