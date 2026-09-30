#!/usr/bin/env bash
# Runs the PerfLens DemoShop sample in the foreground (Ctrl+C to stop).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/samples/PerfLens.DemoShop"

PORT="${DEMOSHOP_PORT:-5080}"
HOST="127.0.0.1"
CONFIGURATION="Debug"
NO_BUILD=0

if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
  C_RESET=$'\033[0m' C_BLUE=$'\033[34m' C_RED=$'\033[31m'
else
  C_RESET="" C_BLUE="" C_RED=""
fi
info() { printf '%s==>%s %s\n' "$C_BLUE" "$C_RESET" "$*"; }
die()  { printf '%serror:%s %s\n' "$C_RED" "$C_RESET" "$*" >&2; exit 1; }

usage() {
  cat <<EOF
Usage: scripts/start-demoshop.sh [options] [PORT]

Runs the DemoShop sample web app in the foreground.

Options:
  -p, --port PORT   Port to listen on (default: \$DEMOSHOP_PORT or 5080)
  --host HOST       Interface to bind (default: 127.0.0.1; use 0.0.0.0 for all)
  --release         Build/run the Release configuration
  --no-build        Skip the build step
  -h, --help        Show this help

Environment:
  DEMOSHOP_PASSWORD  Password for the demo user 'demo' (default: perflens-demo)
EOF
}

while (( $# > 0 )); do
  case "$1" in
    -p|--port) [[ $# -ge 2 ]] || die "$1 needs a value"; PORT="$2"; shift ;;
    --port=*) PORT="${1#*=}" ;;
    --host) [[ $# -ge 2 ]] || die "--host needs a value"; HOST="$2"; shift ;;
    --release) CONFIGURATION="Release" ;;
    --no-build) NO_BUILD=1 ;;
    -h|--help) usage; exit 0 ;;
    [0-9]*) PORT="$1" ;;
    *) die "unknown option '$1' (see --help)" ;;
  esac
  shift
done

if ! [[ "$PORT" =~ ^[0-9]+$ ]] || (( PORT < 1 || PORT > 65535 )); then die "invalid port '$PORT'"; fi
command -v dotnet >/dev/null 2>&1 || die "dotnet not found; run scripts/install.sh first"

args=(run --project "$PROJECT" -c "$CONFIGURATION" --no-launch-profile)
(( NO_BUILD )) && args+=(--no-build)

info "Starting DemoShop on http://$HOST:$PORT (Ctrl+C to stop)"
exec dotnet "${args[@]}" -- --urls "http://$HOST:$PORT"
