#!/usr/bin/env bash
# Application-lifecycle audit campaigns (docs/project/http-conformance.md, "Application
# lifecycle audit"). Same container and conventions as run-campaigns.sh: one git
# archive snapshot, a fresh server process per phase, statistics over stdin, and
# every phase's exit status recorded. Failed phases are kept; nothing is retried.
# Usage: run-lifecycle-campaigns.sh <source-dir> <output-dir> <seed> <scale>
set -uo pipefail
if (( $# < 3 )); then echo "Expected source, output and seed." >&2; exit 2; fi
src=$1
out=$2
seed=$3
scale=${4:-1}
[[ "$scale" =~ ^[1-9][0-9]*$ ]] || { echo "Scale must be a positive integer." >&2; exit 2; }
# Reject typos instead of emitting a successful zero-phase report.
if [[ -n "${ONLY:-}" ]]; then
  selected=false
  for requested in $ONLY; do
    case "$requested" in
      sections|capsules|lifecycle-h2|lifecycle-h2-tls|lifecycle-h3|drain-h2|drain-h3|h2spec) selected=true;;
      *) echo "Unknown phase: $requested" >&2; exit 2;;
    esac
  done
  [[ "$selected" == true ]] || { echo "No phases selected." >&2; exit 2; }
fi
mkdir -p "$out" || exit 2
out=$(cd "$out" && pwd) || exit 2
src=$(cd "$src" && pwd) || exit 2
status=0
record() { printf '%s\n' "$*" | tee -a "$out/summary.txt"; }

record "source=${SOURCE_SHA:-unknown} (git archive snapshot)"
record "seed=$seed scale=$scale cpus=$(nproc) kernel=$(uname -r) started=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
record "dotnet=$(dotnet --version)"
record "h2spec=$(h2spec --version 2>&1 | head -1) python=$(python3 --version)"
record "peers=$(python3 -I -c 'import importlib.metadata as m; print(" ".join(f"{p}={m.version(p)}" for p in ("h2","hyperframe","hpack","aioquic","pylsqpack")))')"

dotnet build "$src/test/EmbedIO.Conformance" -c Release -o /tmp/conformance > "$out/build.log" 2>&1 || { record "BUILD FAILED"; exit 2; }
sha256sum /tmp/conformance/EmbedIO.dll | tee -a "$out/summary.txt"

http=18080; https=18443; h3=18444
phase=""

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/campaign-server.sh" || exit 2

step() {
  local name=$1; shift
  record "-- $name"
  local started=$SECONDS
  "$@" > "$out/$name.log" 2>&1
  local code=$?
  tail -n 3 "$out/$name.log" | cut -c1-600 | tee -a "$out/summary.txt"
  record "exit=$code seconds=$((SECONDS - started))"
  [ $code -eq 0 ] || status=1
  stats "$phase:$name"
}

py() { python3 -I "$src/test/EmbedIO.Conformance/drivers/$1" "${@:2}"; }
cert="$out/conformance-cert.pem"
# ONLY="phase ..." limits a diagnostic rerun to the named phases; default runs all.
want() { [ -z "${ONLY:-}" ] || [[ " $ONLY " == *" $1 "* ]]; }

if want sections; then start_server sections
step sections-h1 py response_sections.py h1 --port $http --cert "$cert"
step sections-h1-tls py response_sections.py h1 --port $https --tls --cert "$cert"
step sections-h2 py response_sections.py h2 --port $http --cert "$cert"
step sections-h2-tls py response_sections.py h2 --port $https --tls --cert "$cert"
step sections-h3 py response_sections.py h3 --host localhost --port $h3 --cert "$cert" --h3-interim-adapter
stop_server; fi

if want capsules; then start_server capsules
step capsule-h2 py capsule_campaign.py h2 --port $http --stats-port $http --out "$out/capsule-h2.json"
step capsule-h2-tls py capsule_campaign.py h2 --port $https --tls --stats-port $http --out "$out/capsule-h2-tls.json"
step capsule-h3 py capsule_campaign.py h3 --host localhost --port $h3 --stats-port $http --out "$out/capsule-h3.json"
stop_server; fi

if want lifecycle-h2; then start_server lifecycle-h2
step lifecycle-h2-fuzz py lifecycle_campaign.py h2 fuzz --port $http --stats-port $http --seed "$seed" --iterations $((300 * scale)) --out "$out/lifecycle-h2-fuzz.json"
stop_server; fi

if want lifecycle-h2-tls; then start_server lifecycle-h2-tls
step lifecycle-h2-tls-fuzz py lifecycle_campaign.py h2 fuzz --port $https --tls --stats-port $http --seed "$seed" --iterations $((100 * scale)) --out "$out/lifecycle-h2-tls-fuzz.json"
stop_server; fi

if want lifecycle-h3; then start_server lifecycle-h3
step lifecycle-h3-fuzz py lifecycle_campaign.py h3 fuzz --host localhost --port $h3 --stats-port $http --seed "$seed" --iterations $((100 * scale)) --out "$out/lifecycle-h3-fuzz.json"
stop_server; fi

# A drain stops its endpoint, so every repetition gets a fresh server process.
for round in $(seq 1 $((10 * scale))); do
  if want drain-h2; then start_server drain-h2-$round
  step drain-h2-tls-$round py lifecycle_campaign.py h2 drain --port $https --tls --stats-port $http --endpoint https --out "$out/drain-h2-tls-$round.json"
  stop_server; fi
  if want drain-h3; then start_server drain-h3-$round
  step drain-h3-$round py lifecycle_campaign.py h3 drain --host localhost --port $h3 --stats-port $http --endpoint h3 --out "$out/drain-h3-$round.json"
  stop_server; fi
done

if want h2spec; then start_server h2spec
step h2spec-h2c h2spec --host 127.0.0.1 --port $http --timeout 3 --junit-report "$out/h2spec-h2c.xml"
step h2spec-tls h2spec --host 127.0.0.1 --port $https --tls --insecure --timeout 3 --junit-report "$out/h2spec-tls.xml"
stop_server; fi

record "finished=$(date -u +%Y-%m-%dT%H:%M:%SZ) overall=$status"
exit $status
