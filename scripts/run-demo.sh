#!/usr/bin/env bash
# Builds and starts DemoShop in the background, benchmarks it with PerfLens and stops it again.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_DIR="$REPO_ROOT/samples/PerfLens.DemoShop"
DLL="$PROJECT_DIR/bin/Release/net10.0/PerfLens.DemoShop.dll"

CONFIG="$REPO_ROOT/configs/demoshop.yaml"
OUTPUT_DIR="perflens-results/demoshop"
PORT="${DEMOSHOP_PORT:-5080}"
NO_BUILD=0
EXTRA_ARGS=()
DEMOSHOP_PID=""
LOG_FILE=""
MARKER=""

if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
  C_RESET=$'\033[0m' C_BOLD=$'\033[1m' C_BLUE=$'\033[34m' C_GREEN=$'\033[32m' C_YELLOW=$'\033[33m' C_RED=$'\033[31m'
else
  C_RESET="" C_BOLD="" C_BLUE="" C_GREEN="" C_YELLOW="" C_RED=""
fi
step() { printf '%s==>%s %s%s%s\n' "$C_BLUE" "$C_RESET" "$C_BOLD" "$*" "$C_RESET"; }
ok()   { printf '    %s✔%s %s\n' "$C_GREEN" "$C_RESET" "$*"; }
warn() { printf '    %s!%s %s\n' "$C_YELLOW" "$C_RESET" "$*" >&2; }
die()  { printf '%serror:%s %s\n' "$C_RED" "$C_RESET" "$*" >&2; exit 1; }

usage() {
  cat <<EOF
Usage: scripts/run-demo.sh [options] [-- perflens run options]

Builds and starts the DemoShop sample in the background, waits for /health,
runs 'perflens run' against it, stops DemoShop and prints the report path.

Options:
  --quick           Use configs/demoshop-quick.json (fast smoke test)
  -c, --config FILE Use another config file
  -p, --port PORT   Preferred port (default: \$DEMOSHOP_PORT or 5080; the next
                    free port is used if it is taken)
  --no-build        Do not rebuild DemoShop
  -h, --help        Show this help

Any other arguments are passed to 'perflens run', e.g.:
  scripts/run-demo.sh --quick --headed
  scripts/run-demo.sh -- --iterations 5 --label nightly --open

Uses 'perflens' from PATH when installed, otherwise 'dotnet run --project src/PerfLens.Cli'.
EOF
}

while (( $# > 0 )); do
  case "$1" in
    --quick) CONFIG="$REPO_ROOT/configs/demoshop-quick.json"; OUTPUT_DIR="perflens-results/demoshop-quick" ;;
    -c|--config) [[ $# -ge 2 ]] || die "$1 needs a value"; CONFIG="$2"; OUTPUT_DIR="perflens-results"; shift ;;
    -p|--port) [[ $# -ge 2 ]] || die "$1 needs a value"; PORT="$2"; shift ;;
    --no-build) NO_BUILD=1 ;;
    -h|--help) usage; exit 0 ;;
    --) shift; EXTRA_ARGS+=("$@"); break ;;
    -o|--output)
      [[ $# -ge 2 ]] || die "$1 needs a value"
      OUTPUT_DIR="$2"; EXTRA_ARGS+=("$1" "$2"); shift ;;
    *) EXTRA_ARGS+=("$1") ;;
  esac
  shift
done

# Also honour -o/--output given after "--".
for (( i = 0; i < ${#EXTRA_ARGS[@]}; i++ )); do
  if [[ "${EXTRA_ARGS[i]}" == -o || "${EXTRA_ARGS[i]}" == --output ]] && (( i + 1 < ${#EXTRA_ARGS[@]} )); then
    OUTPUT_DIR="${EXTRA_ARGS[i + 1]}"
  fi
done

[[ "$PORT" =~ ^[0-9]+$ ]] || die "invalid port '$PORT'"
[[ -f "$CONFIG" ]] || die "config not found: $CONFIG"
command -v dotnet >/dev/null 2>&1 || die "dotnet not found; run scripts/install.sh first"
command -v curl >/dev/null 2>&1 || die "curl is required"

port_in_use() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

# shellcheck disable=SC2317  # invoked via trap
cleanup() {
  local code=$?
  if [[ -n "$DEMOSHOP_PID" ]] && kill -0 "$DEMOSHOP_PID" 2>/dev/null; then
    step "Stopping DemoShop (pid $DEMOSHOP_PID)"
    kill "$DEMOSHOP_PID" 2>/dev/null || true
    for _ in $(seq 1 50); do kill -0 "$DEMOSHOP_PID" 2>/dev/null || break; sleep 0.1; done
    kill -9 "$DEMOSHOP_PID" 2>/dev/null || true
    wait "$DEMOSHOP_PID" 2>/dev/null || true
  fi
  [[ -n "$MARKER" ]] && rm -f "$MARKER"
  [[ -n "$LOG_FILE" && $code -eq 0 ]] && rm -f "$LOG_FILE"
  exit "$code"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"

if (( NO_BUILD )) && [[ -f "$DLL" ]]; then
  step "Using existing DemoShop build"
else
  step "Building DemoShop"
  dotnet build "$PROJECT_DIR" -c Release -v quiet --nologo >/dev/null || die "DemoShop build failed (run: dotnet build $PROJECT_DIR)"
  ok "built"
fi

start_port="$PORT"
while port_in_use "$PORT"; do
  PORT=$((PORT + 1))
  (( PORT < start_port + 100 )) || die "no free port found in $start_port-$PORT"
done
[[ "$PORT" == "$start_port" ]] || warn "port $start_port is busy; using $PORT"
URL="http://127.0.0.1:$PORT"

step "Starting DemoShop on $URL"
LOG_FILE="$(mktemp -t demoshop.XXXXXX.log)"
# Run from the project directory so wwwroot is found (content root).
(cd "$PROJECT_DIR" && exec dotnet "$DLL" --urls "$URL" --contentRoot "$PROJECT_DIR") >"$LOG_FILE" 2>&1 &
DEMOSHOP_PID=$!

for _ in $(seq 1 120); do
  if curl -fsS "$URL/health" >/dev/null 2>&1; then break; fi
  kill -0 "$DEMOSHOP_PID" 2>/dev/null || { tail -n 20 "$LOG_FILE" >&2; die "DemoShop exited during startup (log: $LOG_FILE)"; }
  sleep 0.25
done
curl -fsS "$URL/health" >/dev/null 2>&1 || { tail -n 20 "$LOG_FILE" >&2; die "DemoShop did not become healthy within 30 s (log: $LOG_FILE)"; }
ok "healthy (pid $DEMOSHOP_PID, log $LOG_FILE)"

if command -v perflens >/dev/null 2>&1; then
  PERFLENS=(perflens)
else
  warn "'perflens' not on PATH; using 'dotnet run --project src/PerfLens.Cli' (run scripts/install.sh to install)"
  PERFLENS=(dotnet run --project "$REPO_ROOT/src/PerfLens.Cli" -c Release --)
fi

export DEMOSHOP_URL="$URL"
MARKER="$(mktemp)"
step "Running: perflens run -c ${CONFIG#"$REPO_ROOT"/} ${EXTRA_ARGS[*]:-}"
set +e
(cd "$REPO_ROOT" && "${PERFLENS[@]}" run -c "$CONFIG" --url "$URL" ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"})
status=$?
set -e

case "$OUTPUT_DIR" in /*) out_abs="$OUTPUT_DIR" ;; *) out_abs="$REPO_ROOT/$OUTPUT_DIR" ;; esac
report=""
if [[ -d "$out_abs" ]]; then
  report="$(find "$out_abs" -name '*.html' -newer "$MARKER" -type f 2>/dev/null | sort | tail -n 1)"
fi

case "$status" in
  0) ok "benchmark passed" ;;
  1) warn "benchmark finished with threshold failures (expected for DemoShop)" ;;
  *) warn "perflens exited with code $status" ;;
esac
if [[ -n "$report" ]]; then
  ok "report: $report"
else
  warn "no new HTML report found under $out_abs"
fi
# Threshold failures (exit 1) are the point of the demo; report other failures.
if (( status == 1 )); then exit 0; fi
exit "$status"
