#!/usr/bin/env bash
# Runs every conformance and stateful campaign. Each phase gets a fresh, separate
# server process so a finding that stops a listener cannot invalidate later phases.
# Intended for the pinned container in docker/Dockerfile; see README.md.
# Usage: run-campaigns.sh <source-dir> <output-dir> <seed> <scale>
#   scale multiplies fuzz iterations (1 = quick, 10 = extended).
set -uo pipefail
src=$1
out=$2
seed=$3
scale=${4:-1}
mkdir -p "$out"
status=0
record() { printf '%s\n' "$*" | tee -a "$out/summary.txt"; }

record "source=${SOURCE_SHA:-unknown} (git archive snapshot)"
record "seed=$seed scale=$scale cpus=$(nproc) kernel=$(uname -r)"
record "dotnet=$(dotnet --version) msquic=$(dpkg-query --show --showformat='${Version}' libmsquic 2>/dev/null || echo missing)"
record "h2spec=$(h2spec --version 2>&1 | head -1) python=$(python3 --version)"

python3 -I "$src/test/EmbedIO.Conformance/drivers/settings_ack_test.py" > "$out/peer-settings-ack.log" 2>&1 || { record "PEER SETTINGS MODEL FAILED"; exit 2; }
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

# Statistics travel over stdin, so they remain readable after a listener stops.
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
  "$@" > "$out/$name.log" 2>&1
  local code=$?
  tail -n 3 "$out/$name.log" | tee -a "$out/summary.txt"
  record "exit=$code"
  [ $code -eq 0 ] || status=1
  stats "$phase:$name"
}

py() { python3 -I "$src/test/EmbedIO.Conformance/drivers/$1" "${@:2}"; }
host() { dotnet /tmp/conformance/EmbedIO.Conformance.dll "$@"; }

start_server h1
step h1-cases host h1 --port $http --out "$out/h1-cases.json"
step h1-fuzz host h1-fuzz --port $http --seed "$seed" --iterations $((2000 * scale)) --out "$out"
stop_server

start_server h2spec
step h2spec-h2c h2spec --host 127.0.0.1 --port $http --timeout 3 --junit-report "$out/h2spec-h2c.xml"
step h2spec-tls h2spec --host 127.0.0.1 --port $https --tls --insecure --timeout 3 --junit-report "$out/h2spec-tls.xml"
stop_server

start_server h2-cases
step h2-cases py h2_campaign.py cases --port $http --stats-port $http --out "$out/h2-cases.json"
stop_server

start_server h2-tls-cases
step h2-tls-cases py h2_campaign.py cases --port $https --tls --stats-port $http --out "$out/h2-tls-cases.json"
stop_server

start_server h2-fuzz
step h2-fuzz py h2_campaign.py fuzz --port $http --seed "$seed" --iterations $((300 * scale)) --stats-port $http --out "$out/h2-fuzz.json"
stop_server

start_server h3-fuzz
step h3-fuzz py h3_campaign.py fuzz --host localhost --port $h3 --seed "$seed" --iterations $((100 * scale)) --stats-port $http --out "$out/h3-fuzz.json"
stop_server

start_server h3-cases
step h3-cases py h3_campaign.py cases --host localhost --port $h3 --stats-port $http --out "$out/h3-cases.json"
stop_server

record "overall=$status"
exit $status
