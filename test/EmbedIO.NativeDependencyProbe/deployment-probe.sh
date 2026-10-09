#!/usr/bin/env bash
# Private local-feed deployment proof; never publishes or installs system libraries.
set -euo pipefail
test "$(uname -s)" = Darwin
test "$(uname -m)" = arm64
repo="$PWD"
candidate="$1"
results="$2"
fixture="$repo/test/EmbedIO.NativeDependencyProbe"
mkdir -p "$results/feed" "$results/consumer"
feed="$results/feed"
project="$results/consumer/EmbedIO.NativeDependencyProbe.csproj"
cp "$fixture/Probe/EmbedIO.NativeDependencyProbe.csproj.template" "$project"
cat > "$results/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="candidate" value="$feed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
EOF
dotnet restore "$fixture/Package/NativeCandidate.csproj" --locked-mode > "$results/pack-restore.log" 2>&1
dotnet pack "$fixture/Package/NativeCandidate.csproj" -c Release --no-restore \
  "-p:CandidateRoot=$candidate" -o "$feed" > "$results/pack.log" 2>&1
package="$feed/EmbedIO-Neo.Native.MsQuic.Candidate.0.0.0-local.nupkg"
python3 "$fixture/verify_package.py" "$candidate" "$package" > "$results/package-verification.json"
dotnet restore "$project" --configfile "$results/NuGet.Config" --packages "$RUNNER_TEMP/native-probe-packages" \
  --force-evaluate "-p:ProbeSourceRoot=$repo" > "$results/portable-restore.log" 2>&1
dotnet restore "$project" --configfile "$results/NuGet.Config" --packages "$RUNNER_TEMP/native-probe-packages" \
  --locked-mode "-p:ProbeSourceRoot=$repo" > "$results/portable-locked-restore.log" 2>&1
cp "$results/consumer/packages.lock.json" "$results/portable.lock.json"
dotnet build "$project" -c Release --no-restore "-p:ProbeSourceRoot=$repo" > "$results/portable-build.log" 2>&1
portable="$results/consumer/bin/Release/net10.0"
run_consumer() {
  local label="$1" executable="$2" native_prefix="$3"
  local output="$results/$label-probe"
  mkdir -p "$output"
  if test "$label" = portable; then
    env -u DYLD_LIBRARY_PATH -u DYLD_FALLBACK_LIBRARY_PATH -u DYLD_INSERT_LIBRARIES \
      -u EMBEDIO_QUIC_TRACE_LIBRARY -u EMBEDIO_QUIC_TRACE_OUTPUT DYLD_PRINT_LIBRARIES=1 \
      dotnet "$executable" --quic-rebind 4096 "$output" > "$output/stdout.log" 2> "$output/loader.log"
  else
    env -u DYLD_LIBRARY_PATH -u DYLD_FALLBACK_LIBRARY_PATH -u DYLD_INSERT_LIBRARIES \
      -u EMBEDIO_QUIC_TRACE_LIBRARY -u EMBEDIO_QUIC_TRACE_OUTPUT DYLD_PRINT_LIBRARIES=1 \
      "$executable" --quic-rebind 4096 "$output" > "$output/stdout.log" 2> "$output/loader.log"
  fi
  grep -F "$native_prefix/libmsquic" "$output/loader.log" > "$output/loaded-library.txt"
  jq -e '.passed and .traceComplete and .completed == 4096' "$output/managed.json" > /dev/null
  shasum -a 256 "$native_prefix"/libmsquic*.dylib > "$output/native-assets.sha256"
  local expected
  expected="$(jq -r .sha256 "$candidate/build-receipt.json")"
  test "$(awk '{print $1}' "$output/native-assets.sha256" | sort -u)" = "$expected"
}
run_consumer portable "$portable/EmbedIO.NativeDependencyProbe.dll" "$portable/runtimes/osx-arm64/native"
dotnet restore "$project" -r osx-arm64 --configfile "$results/NuGet.Config" --packages "$RUNNER_TEMP/native-probe-packages" \
  --force-evaluate "-p:ProbeSourceRoot=$repo" > "$results/publish-restore.log" 2>&1
dotnet restore "$project" -r osx-arm64 --configfile "$results/NuGet.Config" --packages "$RUNNER_TEMP/native-probe-packages" \
  --locked-mode "-p:ProbeSourceRoot=$repo" > "$results/publish-locked-restore.log" 2>&1
cp "$results/consumer/packages.lock.json" "$results/publish.lock.json"
dotnet publish "$project" -c Release -r osx-arm64 --self-contained false --no-restore \
  "-p:ProbeSourceRoot=$repo" -o "$results/publish" > "$results/publish.log" 2>&1
run_consumer published "$results/publish/EmbedIO.NativeDependencyProbe" "$results/publish"