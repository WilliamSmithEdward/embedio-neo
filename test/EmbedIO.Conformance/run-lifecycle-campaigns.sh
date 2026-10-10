#!/usr/bin/env bash
# Application-lifecycle audit campaigns (docs/project/http-conformance.md, "Application
# lifecycle audit"). Same container and conventions as run-campaigns.sh: one git
# archive snapshot, a fresh server process per phase, statistics over stdin, and
# every phase's exit status recorded. Failed phases are kept; nothing is retried.
# Usage: run-lifecycle-campaigns.sh <source-dir> <output-dir> <seed> <scale>
set -uo pipefail
src=$1
out=$2
seed=$3
scale=${4:-1}
mkdir -p "$out"
status=0
record() { printf '%s\n' "$*" | tee -a "$out/summary.txt"; }

record "source=${SOURCE_SHA:-unknown} (git archive snapshot)"
record "seed=$seed scale=$scale cpus=$(nproc) kernel=$(uname -r) started=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
record "dotnet=$(dotnet --version) msquic=$(dpkg-query --show --showformat='${Version}' libmsquic 2>/dev/null || echo missing)"
record "h2spec=$(h2spec --version 2>&1 | head -1) python=$(python3 --version)"
record "peers=$(python3 -I -c 'import importlib.metadata as m; print(" ".join(f"{p}={m.version(p)}" for p in ("h2","hyperframe","hpack","aioquic","pylsqpack")))')"

dotnet build "$src/test/EmbedIO.Conformance" -c Release -o /tmp/conformance > "$out/build.log" 2>&1 || { record "BUILD FAILED"; exit 2; }
sha256sum /tmp/conformance/EmbedIO.dll | tee -a "$out/summary.txt"

http=18080; https=18443; h3=18444
phase=""

start_server() {
  phase=$1
  coproc SERVER { cd "$out" && exec dotnet /tmp/conformance/EmbedIO.Conformance.dll serve --http $http --https $https --h3 $h3 --log --cert-out "$out/conformance-cert.pem" 2> "$out/server-$phase.stderr.log"; }
  exec 3>&"${SERVER[1]}" 4<&"${SERVER[0]}"
  local ready=""
  while read -r -t 60 line <&4; do
    printf '%s\n' "$line" >> "$out/server-$phase.log"
    case $line in READY*) ready=$line;; IDENTITY*) break;; esac
  done
  [ -n "$ready" ] || { record "SERVER DID NOT START for $phase"; exit 2; }
  record "== phase $phase: $ready"
  stats "$phase:start"
}

stats() { echo stats >&3; read -r -t 30 line <&4; printf '%s %s\n' "$1" "$line" | tee -a "$out/stats.log"; }

stop_server() {
  echo stop >&3
  while read -r -t 30 line <&4; do printf '%s\n' "$line" >> "$out/server-$phase.log"; done
  wait "$SERVER_PID"
  local code=$?
  exec 3>&- 4<&-
  record "server $phase exit=$code $(grep -h ENDPOINT-STOPPED "$out/server-$phase.log" | tr '\n' ' ')"
  [ $code -eq 0 ] || status=1
}

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

if want drain-h2; then start_server drain-h2
step drain-h2-tls py lifecycle_campaign.py h2 drain --port $https --tls --stats-port $http --endpoint https --out "$out/drain-h2-tls.json"
stop_server; fi

if want drain-h3; then start_server drain-h3
step drain-h3 py lifecycle_campaign.py h3 drain --host localhost --port $h3 --stats-port $http --endpoint h3 --out "$out/drain-h3.json"
stop_server; fi

if want h2spec; then start_server h2spec
step h2spec-h2c h2spec --host 127.0.0.1 --port $http --timeout 3 --junit-report "$out/h2spec-h2c.xml"
step h2spec-tls h2spec --host 127.0.0.1 --port $https --tls --insecure --timeout 3 --junit-report "$out/h2spec-tls.xml"
stop_server; fi

record "finished=$(date -u +%Y-%m-%dT%H:%M:%SZ) overall=$status"
exit $status
