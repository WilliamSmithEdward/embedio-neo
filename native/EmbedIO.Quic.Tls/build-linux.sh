#!/usr/bin/env bash
set -euo pipefail
mkdir -p /results/quic-native-build-logs
trap 'cp /tmp/native-build/*.log /results/quic-native-build-logs/ 2>/dev/null || true' EXIT
/opt/python/cp312-cp312/bin/python /source/build.py --source /source --work /tmp/native-build --output /results/quic-native --jobs 3 --rid "${1:?Runtime identifier required}"
