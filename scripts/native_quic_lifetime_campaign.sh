#!/usr/bin/env bash
# Runs the explicit native MsQuic lifetime campaigns once, without retries,
# under the shared TestResults/BENCHMARK-LOCK.txt of the primary checkout.
#
# usage: scripts/native_quic_lifetime_campaign.sh [--iterations N] [--seed S]
#            [--filter EXPR] [--label NAME] [--no-build] [--library-dir DIR]
#
# --library-dir (or EMBEDIO_QUIC_LIBRARY_DIR) names the directory holding the
# pinned MsQuic library. macOS strips DYLD_* variables when a protected binary
# such as /usr/bin/env starts this script, so the loader path is exported here.
# EMBEDIO_REQUIRE_QUIC=1 is set so a missing provider fails instead of
# skipping. Every attempt keeps its own directory under TestResults/native-quic-lifetime; a failed run is never
# repeated by this script. See docs/project/http3-native-lifetime-campaigns.md.
set -euo pipefail

iterations=200
seed=20261010
filter="TestCategory=NativeQuicLifetimeCampaign"
label=campaign
build=1
library="${EMBEDIO_QUIC_LIBRARY_DIR:-}"
while test $# -gt 0; do
  case "$1" in
    --iterations) iterations="$2"; shift 2 ;;
    --seed) seed="$2"; shift 2 ;;
    --filter) filter="$2"; shift 2 ;;
    --label) label="$2"; shift 2 ;;
    --no-build) build=0; shift ;;
    --library-dir) library="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 64 ;;
  esac
done

if test -n "$library"; then
  case "$(uname -s)" in
    Darwin) export DYLD_FALLBACK_LIBRARY_PATH="$library" ;;
    *) export LD_LIBRARY_PATH="$library${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" ;;
  esac
fi
root="$(git rev-parse --show-toplevel)"
cd "$root"
sha="$(git rev-parse HEAD)"
common="$(git rev-parse --path-format=absolute --git-common-dir)"
lock="$(dirname "$common")/TestResults/BENCHMARK-LOCK.txt"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
output="$root/TestResults/native-quic-lifetime/$stamp-${sha:0:12}-$label"
mkdir -p "$output" "$(dirname "$lock")"

owner="native-quic-lifetime $sha pid $$"
# noclobber gives O_EXCL creation: another owner's lock is never replaced.
if ! (set -o noclobber; printf '%s\nstarted %s\n' "$owner" "$stamp" > "$lock") 2>/dev/null; then
  echo "BENCHMARK-LOCK.txt is held; not starting:" >&2
  cat "$lock" >&2 || true
  printf 'lock held by another owner at %s\n' "$stamp" > "$output/NOT-STARTED.txt"
  exit 75
fi
release() { if test "$(head -n 1 "$lock" 2>/dev/null)" = "$owner"; then rm -f "$lock"; fi; }
trap release EXIT

{
  echo "source $sha"
  echo "label $label"
  echo "iterations $iterations"
  echo "seed $seed"
  echo "filter $filter"
  echo "started $stamp"
  uname -a
  if command -v sw_vers > /dev/null; then sw_vers; sysctl -n machdep.cpu.brand_string 2>/dev/null || true; fi
  dotnet --version
  dotnet --list-runtimes
  echo "DYLD_FALLBACK_LIBRARY_PATH=${DYLD_FALLBACK_LIBRARY_PATH:-}"
  echo "LD_LIBRARY_PATH=${LD_LIBRARY_PATH:-}"
  echo "foreign processes:"
  ps -eo pid=,args= | grep -E 'EmbedIO\.Tests|LoadBenchmark|EmbedIO\.Fuzz|dotnet test' | grep -v -e grep -e "$$" || true
} > "$output/environment.txt" 2>&1

if test "$build" = 1; then
  dotnet build test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release > "$output/build.log" 2>&1
fi

status=0
EMBEDIO_REQUIRE_QUIC=1 \
EMBEDIO_SOURCE_SHA="$sha" \
EMBEDIO_NATIVE_QUIC_CAMPAIGN_ITERATIONS="$iterations" \
EMBEDIO_NATIVE_QUIC_CAMPAIGN_SEED="$seed" \
EMBEDIO_NATIVE_QUIC_CAMPAIGN_OUTPUT="$output" \
  dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-build \
    --report-trx --results-directory "$output" --timeout 60m --filter "$filter" \
    --output detailed > "$output/run.log" 2>&1 || status=$?
printf 'exit %s\nfinished %s\n' "$status" "$(date -u +%Y%m%dT%H%M%SZ)" >> "$output/environment.txt"
echo "$output (exit $status)"
exit "$status"
