#!/usr/bin/env bash
# Focused reproduction: start the host with engine logging and run one driver mode.
# Usage: repro.sh <source-dir> <output-dir> <driver> <driver arguments...>
#   driver is h1 | h1-fuzz | h2 | h3; the server listens on 18080 (http), 18443 (https), 18444 (h3).
set -uo pipefail
if (( $# < 3 )); then echo 'Expected source directory, output directory and driver.' >&2; exit 64; fi
src=$1; out=$2; driver=$3; shift 3
case $driver in h1|h1-fuzz|h2|h3) ;; *) echo "Unknown driver: $driver" >&2; exit 64;; esac
mkdir -p "$out" || exit 2
out=$(cd "$out" && pwd) || exit 2
src=$(cd "$src" && pwd) || exit 2
dotnet build "$src/test/EmbedIO.Conformance" -c Release -o /tmp/conformance > "$out/build.log" 2>&1 || { echo "BUILD FAILED"; exit 2; }
coproc SERVER { cd "$out" && exec dotnet /tmp/conformance/EmbedIO.Conformance.dll serve --http 18080 --https 18443 --h3 18444 --log 2> "$out/server-stderr.log"; }
server_pid=$SERVER_PID
# Capture descriptors and PID before Bash unsets coprocess variables on exit.
cleanup() {
  code=$?
  trap - EXIT
  if [[ -n "$server_pid" ]]; then
    kill -KILL "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  exit "$code"
}
trap cleanup EXIT
exec 3>&"${SERVER[1]}" 4<&"${SERVER[0]}" || exit 2
ready=false
deadline=$((SECONDS + 60))
while (( SECONDS < deadline )); do
  IFS= read -r -t "$((deadline - SECONDS))" line <&4 || break
  printf '%s\n' "$line" >> "$out/server.log"
  case $line in IDENTITY*) ready=true; break;; esac
done
if [[ "$ready" != true ]]; then echo 'Server did not report readiness.' >&2; exit 1; fi
case $driver in
  h1|h1-fuzz) dotnet /tmp/conformance/EmbedIO.Conformance.dll "$driver" --port 18080 "$@" ;;
  h2) python3 -I "$src/test/EmbedIO.Conformance/drivers/h2_campaign.py" "$@" ;;
  h3) python3 -I "$src/test/EmbedIO.Conformance/drivers/h3_campaign.py" "$@" ;;
esac 2>&1 | tee "$out/driver.log"
statuses=("${PIPESTATUS[@]}")
code=${statuses[0]}
if (( code == 0 && statuses[1] != 0 )); then code=${statuses[1]}; fi
printf 'stop\n' >&3 || { if (( code == 0 )); then code=1; fi; }
exec 3>&-
deadline=$((SECONDS + 30))
while (( SECONDS < deadline )); do
  IFS= read -r -t "$((deadline - SECONDS))" line <&4 || break
  printf '%s\n' "$line" >> "$out/server.log"
done
# EOF is not proof of process exit: a peer can close stdout before it stops.
while kill -0 "$server_pid" 2>/dev/null; do
  if (( SECONDS >= deadline )); then echo 'Server shutdown timed out.' >&2; exit 124; fi
  sleep 0.05
done
wait "$server_pid"
server_code=$?
server_pid=
echo "server exit=$server_code driver exit=$code"
if (( code != 0 )); then exit "$code"; fi
exit "$server_code"
