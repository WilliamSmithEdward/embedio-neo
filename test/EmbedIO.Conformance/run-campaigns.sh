#!/usr/bin/env bash
# Runs every conformance and stateful campaign against one separate server process.
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

record "source=$(git -C "$src" rev-parse HEAD 2>/dev/null || echo unknown) dirty=$(git -C "$src" status --porcelain 2>/dev/null | wc -l)"
record "seed=$seed scale=$scale cpus=$(nproc) kernel=$(uname -r)"
record "dotnet=$(dotnet --version) msquic=$(dpkg-query --show --showformat='${Version}' libmsquic 2>/dev/null || echo missing)"
record "h2spec=$(h2spec --version 2>&1 | head -1) python=$(python3 --version)"

dotnet build "$src/test/EmbedIO.Conformance" -c Release -o /tmp/conformance > "$out/build.log" 2>&1 || { record "BUILD FAILED"; exit 2; }
sha256sum /tmp/conformance/EmbedIO.dll | tee -a "$out/summary.txt"

http=18080; https=18443; h3=18444
coproc SERVER { cd "$out" && exec dotnet /tmp/conformance/EmbedIO.Conformance.dll serve --http $http --https $https --h3 $h3 2> "$out/server-stderr.log"; }
exec 3>&"${SERVER[1]}"
ready=""
while read -r -t 60 line <&"${SERVER[0]}"; do
  printf '%s\n' "$line" >> "$out/server.log"
  case $line in READY*) ready=$line;; IDENTITY*) break;; esac
done
[ -n "$ready" ] || { record "SERVER DID NOT START"; exit 2; }
record "$ready"

stats() { echo stats >&3; read -r -t 30 line <&"${SERVER[0]}"; printf '%s %s\n' "$1" "$line" | tee -a "$out/stats.log"; }
step() {
  local name=$1; shift
  record "== $name"
  "$@" > "$out/$name.log" 2>&1
  local code=$?
  tail -n 3 "$out/$name.log" | tee -a "$out/summary.txt"
  record "exit=$code"
  [ $code -eq 0 ] || status=1
  stats "$name"
}

stats start
step h1-cases dotnet /tmp/conformance/EmbedIO.Conformance.dll h1 --port $http --out "$out/h1-cases.json"
step h1-fuzz dotnet /tmp/conformance/EmbedIO.Conformance.dll h1-fuzz --port $http --seed "$seed" --iterations $((2000 * scale)) --out "$out"
step h2spec-h2c h2spec --host 127.0.0.1 --port $http --timeout 3 --junit-report "$out/h2spec-h2c.xml"
step h2spec-tls h2spec --host 127.0.0.1 --port $https --tls --insecure --timeout 3 --junit-report "$out/h2spec-tls.xml"
step h2-cases python3 -I "$src/test/EmbedIO.Conformance/drivers/h2_campaign.py" cases --port $http --stats-port $http --out "$out/h2-cases.json"
step h2-tls-cases python3 -I "$src/test/EmbedIO.Conformance/drivers/h2_campaign.py" cases --port $https --tls --stats-port $http --out "$out/h2-tls-cases.json"
step h2-fuzz python3 -I "$src/test/EmbedIO.Conformance/drivers/h2_campaign.py" fuzz --port $http --seed "$seed" --iterations $((300 * scale)) --stats-port $http --out "$out/h2-fuzz.json"
step h3-cases python3 -I "$src/test/EmbedIO.Conformance/drivers/h3_campaign.py" cases --host localhost --port $h3 --stats-port $http --out "$out/h3-cases.json"
step h3-fuzz python3 -I "$src/test/EmbedIO.Conformance/drivers/h3_campaign.py" fuzz --host localhost --port $h3 --seed "$seed" --iterations $((100 * scale)) --stats-port $http --out "$out/h3-fuzz.json"

echo stop >&3
while read -r -t 30 line <&"${SERVER[0]}"; do printf '%s\n' "$line" >> "$out/server.log"; done
wait "$SERVER_PID"; record "server exit=$?"
record "overall=$status"
exit $status
