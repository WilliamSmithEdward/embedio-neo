#!/usr/bin/env bash
# Shared process ownership for development-only conformance wrappers.
server_pid=""
cleanup_server() {
  local code=$?
  trap - EXIT
  if [[ -n "$server_pid" ]]; then
    kill -KILL "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  exit "$code"
}
trap cleanup_server EXIT

start_server() {
  phase=$1
  coproc SERVER { cd "$out" && exec dotnet /tmp/conformance/EmbedIO.Conformance.dll serve --http "$http" --https "$https" --h3 "$h3" "${provider_args[@]}" --log --cert-out "$out/conformance-cert.pem" 2> "$out/server-$phase.stderr.log"; }
  server_pid=$SERVER_PID
  exec 3>&"${SERVER[1]}" 4<&"${SERVER[0]}" || exit 2
  local ready="" identity=false line deadline=$((SECONDS + 60))
  while (( SECONDS < deadline )); do
    IFS= read -r -t "$((deadline - SECONDS))" line <&4 || break
    printf '%s\n' "$line" >> "$out/server-$phase.log"
    case $line in READY*) ready=$line;; IDENTITY*) identity=true; break;; esac
  done
  if [[ -z "$ready" || "$identity" != true ]]; then record "SERVER DID NOT START for $phase"; exit 2; fi
  record "== phase $phase: $ready"
  stats "$phase:start" || exit 2
}

# Statistics stay available over stdin after a listener stops.
stats() {
  local line
  if ! printf 'stats\n' >&3 || ! IFS= read -r -t 30 line <&4 || [[ "$line" != STATS* ]]; then
    record "MISSING STATS for $1"
    status=1
    return 1
  fi
  printf '%s %s\n' "$1" "$line" | tee -a "$out/stats.log"
}

stop_server() {
  local line deadline=$((SECONDS + 30)) code
  printf 'stop\n' >&3 || status=1
  exec 3>&-
  while (( SECONDS < deadline )); do
    IFS= read -r -t "$((deadline - SECONDS))" line <&4 || break
    printf '%s\n' "$line" >> "$out/server-$phase.log"
  done
  while kill -0 "$server_pid" 2>/dev/null; do
    if (( SECONDS >= deadline )); then record "SERVER STOP TIMED OUT for $phase"; exit 124; fi
    sleep 0.05
  done
  wait "$server_pid"
  code=$?
  server_pid=""
  exec 4<&-
  record "server $phase exit=$code $(grep -h ENDPOINT-STOPPED "$out/server-$phase.log" | tr '\n' ' ')"
  [ "$code" -eq 0 ] || status=1
}
