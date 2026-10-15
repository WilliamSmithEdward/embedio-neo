#!/usr/bin/env bash
set -euo pipefail
seconds=${CAMPAIGN_SECONDS:-60}
seed=${CAMPAIGN_SEED:-1729}
[[ $seconds =~ ^[0-9]+$ && $seed =~ ^[0-9]+$ ]]
(( seconds >= 1 && seconds <= 1800 && seed >= 1 && seed <= 2147483647 ))
mkdir -p /work /results/maps
# AFL creates private files as container root; preserve readable evidence for
# the host runner on successful and failed campaigns alike.
trap 'result=$?; chmod -R a+rX /results; exit "$result"' EXIT
tar -C /input --exclude=bin --exclude=obj -cf - src test/EmbedIO.SharpFuzz .editorconfig Directory.Build.props Directory.Packages.props global.json README.md LICENSE images | tar -xf - -C /work
cd /work
# Match a Linux Git checkout when the read-only source comes from Windows.
find src test -name '*.cs' -type f -exec sed -i 's/\r$//' {} +
dotnet restore test/EmbedIO.SharpFuzz --locked-mode > /results/restore.log 2>&1
dotnet publish test/EmbedIO.SharpFuzz -c Release --no-restore -o /work/publish -m:1 > /results/build.log 2>&1
sha256sum src/EmbedIO/bin/Release/net10.0/EmbedIO.dll > /results/ordinary-before.sha256
before=$(sha256sum /work/publish/EmbedIO.dll)
DOTNET_ROLL_FORWARD=Major /opt/sharpfuzz/sharpfuzz /work/publish/EmbedIO.dll EmbedIO.Utilities.UrlPath EmbedIO.Utilities.UrlEncodedDataParser > /results/instrumentation.log 2>&1
test "$before" != "$(sha256sum /work/publish/EmbedIO.dll)"
sha256sum --check /results/ordinary-before.sha256
mkdir /work/corpus
cp test/EmbedIO.SharpFuzz/corpus/* /work/corpus/
for input in /work/corpus/*; do
    dotnet /work/publish/EmbedIO.SharpFuzz.dll "$input"
done
# The dotnet host has no native AFL signature; SharpFuzz supplies the managed
# shared-memory instrumentation and forkserver. Never use non-instrumented mode.
export AFL_SKIP_BIN_CHECK=1 AFL_NO_AFFINITY=1 AFL_SKIP_CPUFREQ=1 AFL_NO_UI=1
afl-showmap -t 10000 -m none -i /work/corpus -o /results/maps -- dotnet /work/publish/EmbedIO.SharpFuzz.dll --afl > /results/showmap.log 2>&1 || { cat /results/showmap.log; exit 1; }
test -s /results/maps/root && test -s /results/maps/query
if cmp -s /results/maps/root /results/maps/query; then
    echo 'Distinct parser inputs produced identical coverage maps.' >&2
    exit 1
fi
printf 'seconds=%s\nseed=%s\nafl_commit=dbaf11913c1b2702dee5b4d3dcfffd52f1defe50\n' "$seconds" "$seed" > /results/campaign.txt
set +e
timeout "$((seconds + 120))" afl-fuzz -V "$seconds" -s "$seed" -G 4096 -t 10000 -m none -i /work/corpus -o /results/findings -- dotnet /work/publish/EmbedIO.SharpFuzz.dll --afl > /results/campaign.log 2>&1
result=$?
set -e
test "$result" -eq 0 || { cat /results/campaign.log; exit "$result"; }
test -s /results/findings/default/fuzzer_stats
if test -n "$(find /results/findings/default/crashes /results/findings/default/hangs -type f ! -name README.txt -print -quit)"; then
    echo 'AFL++ retained a crash or hang; inspect findings and replay it.' >&2
    exit 1
fi
awk -F: '/execs_done/ { if (($2 + 0) > 0) ok=1 } END { exit !ok }' /results/findings/default/fuzzer_stats
cat /results/findings/default/fuzzer_stats
