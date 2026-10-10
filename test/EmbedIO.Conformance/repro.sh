#!/usr/bin/env bash
# Focused reproduction: start the host with engine logging and run one driver mode.
# Usage: repro.sh <source-dir> <output-dir> <driver> <driver arguments...>
#   driver is h1 | h1-fuzz | h2 | h3; the server listens on 18080 (http), 18443 (https), 18444 (h3).
set -uo pipefail
src=$1; out=$2; driver=$3; shift 3
mkdir -p "$out"
dotnet build "$src/test/EmbedIO.Conformance" -c Release -o /tmp/conformance > "$out/build.log" 2>&1 || { echo "BUILD FAILED"; exit 2; }
coproc SERVER { cd "$out" && exec dotnet /tmp/conformance/EmbedIO.Conformance.dll serve --http 18080 --https 18443 --h3 18444 --log 2> "$out/server-stderr.log"; }
exec 3>&"${SERVER[1]}"
while read -r -t 60 line <&"${SERVER[0]}"; do printf '%s\n' "$line" >> "$out/server.log"; case $line in IDENTITY*) break;; esac; done
case $driver in
  h1|h1-fuzz) dotnet /tmp/conformance/EmbedIO.Conformance.dll "$driver" --port 18080 "$@" ;;
  h2) python3 -I "$src/test/EmbedIO.Conformance/drivers/h2_campaign.py" "$@" ;;
  h3) python3 -I "$src/test/EmbedIO.Conformance/drivers/h3_campaign.py" "$@" ;;
esac 2>&1 | tee "$out/driver.log"
code=${PIPESTATUS[0]}
echo stop >&3
while read -r -t 30 line <&"${SERVER[0]}"; do printf '%s\n' "$line" >> "$out/server.log"; done
wait "$SERVER_PID"; echo "server exit=$? driver exit=$code"
