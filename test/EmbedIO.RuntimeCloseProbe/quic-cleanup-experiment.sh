#!/usr/bin/env bash
# Test-only experiment. Never installs or packages the candidate dependency.
set -euo pipefail
test "$(uname -s)" = Darwin
test "$(uname -m)" = arm64
repo="$PWD"
command -v jq
results="$repo/TestResults/quic-cleanup-experiment"
source_dir="$RUNNER_TEMP/msquic-cleanup-source"
revision=819ab74f851ee168504cbc392ec32e7bed1d82e9
mkdir -p "$results"
test ! -e "$source_dir"
git init "$source_dir"
git -C "$source_dir" remote add origin https://github.com/microsoft/msquic.git
git -C "$source_dir" fetch --depth 1 origin "$revision"
git -C "$source_dir" checkout --detach FETCH_HEAD
test "$(git -C "$source_dir" rev-parse HEAD)" = "$revision"
git -C "$source_dir" submodule update --init --depth 1 submodules/quictls submodules/googletest
test "$(git -C "$source_dir/submodules/quictls" rev-parse HEAD)" = ff36838bb69801cad56823159a036977bcbe5c75
test "$(git -C "$source_dir/submodules/googletest" rev-parse HEAD)" = fa005b296f90faec4f352d7ab382287bf6548c8d
git -C "$source_dir" submodule status > "$results/submodules.txt"
git -C "$source_dir" rev-parse HEAD > "$results/source.txt"
clang --version > "$results/compiler.txt"
cmake --version > "$results/cmake.txt"
brew list --versions openssl@3 > "$results/openssl.txt"
openssl_root="$(brew --prefix openssl@3)"
"$openssl_root/bin/openssl" version -a >> "$results/openssl.txt"
shasum -a 256 "$openssl_root/lib/libcrypto.3.dylib" >> "$results/openssl.txt"
patch_file="$repo/test/EmbedIO.RuntimeCloseProbe/msquic-kqueue-close.patch"
cp "$patch_file" "$results/candidate.patch"
shasum -a 256 "$patch_file" > "$results/patch.sha256"
# Compile the same deterministic lifetime fixture into both variants.
fixture="$repo/test/EmbedIO.RuntimeCloseProbe/kqueue-lifetime-test.inc"
cp "$fixture" "$results/kqueue-lifetime-test.inc"
shasum -a 256 "$fixture" > "$results/lifetime-fixture.sha256"
printf '\n#include "%s"\n' "$fixture" >> "$source_dir/src/platform/unittest/DataPathTest.cpp"

dotnet restore test/EmbedIO.RuntimeCloseProbe/EmbedIO.RuntimeCloseProbe.csproj --locked-mode
dotnet build test/EmbedIO.RuntimeCloseProbe/EmbedIO.RuntimeCloseProbe.csproj -c Release --no-restore
dotnet restore EmbedIO.sln --locked-mode
dotnet build test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-restore
probe="$repo/test/EmbedIO.RuntimeCloseProbe/bin/Release/net10.0/EmbedIO.RuntimeCloseProbe"
trace_library="$RUNNER_TEMP/libembedio_quic_trace.dylib"
clang -std=c11 -O2 -Wall -Wextra -Werror -dynamiclib \
  test/EmbedIO.RuntimeCloseProbe/quic_trace.c -o "$trace_library"
codesign --force --sign - "$trace_library"
shasum -a 256 "$trace_library" > "$results/tracer.sha256"

build_native() {
  local variant="$1"
  local build_dir="$RUNNER_TEMP/msquic-$variant-build"
  cmake -S "$source_dir" -B "$build_dir" \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH="$openssl_root" \
    -DQUIC_TLS_LIB=quictls -DQUIC_USE_SYSTEM_LIBCRYPTO=ON \
    -DQUIC_BUILD_TEST=ON -DQUIC_BUILD_TOOLS=OFF -DQUIC_BUILD_PERF=OFF \
    -DQUIC_ENABLE_LOGGING=OFF -DFETCHCONTENT_FULLY_DISCONNECTED=ON \
    -DQUIC_OUTPUT_DIR="$build_dir/bin" 2>&1 | tee "$results/$variant-configure.log"
  cmake --build "$build_dir" --parallel 3 --target msquic msquicplatformtest \
    2>&1 | tee "$results/$variant-build.log"
  cp "$build_dir/CMakeCache.txt" "$results/$variant-CMakeCache.txt"
  codesign --force --sign - "$build_dir/bin/libmsquic.2.6.2.dylib"
  shasum -a 256 "$build_dir/bin/libmsquic.2.6.2.dylib" > "$results/$variant-library.sha256"
  otool -L "$build_dir/bin/libmsquic.2.6.2.dylib" > "$results/$variant-imports.txt"
}

run_probe() {
  local variant="$1" iteration="$2" traced="$3"
  local output="$results/$variant-$traced-$iteration"
  local library_dir="$RUNNER_TEMP/msquic-$variant-build/bin"
  mkdir -p "$output" || return 2
  local exit_code=0
  if test "$traced" = traced; then
    env DYLD_FALLBACK_LIBRARY_PATH="$library_dir" DYLD_PRINT_LIBRARIES=1 \
      DYLD_INSERT_LIBRARIES="$trace_library" EMBEDIO_QUIC_TRACE_LIBRARY="$trace_library" \
      EMBEDIO_QUIC_TRACE_OUTPUT="$output/native.jsonl" \
      "$probe" --quic-rebind 4096 "$output" > "$output/stdout.log" 2> "$output/loader.log" || exit_code=$?
  else
    env DYLD_FALLBACK_LIBRARY_PATH="$library_dir" DYLD_PRINT_LIBRARIES=1 \
      "$probe" --quic-rebind 4096 "$output" > "$output/stdout.log" 2> "$output/loader.log" || exit_code=$?
  fi
  grep -F "$library_dir/libmsquic" "$output/loader.log" > "$output/loaded-library.txt" || return 2
  test -s "$output/managed.json" || return 2
  if test "$exit_code" = 0; then
    jq -e '.passed and .traceComplete and .completed == .iterations' "$output/managed.json" > /dev/null || return 2
  elif test "$exit_code" = 1 && test "$variant" = control; then
    # Accept only the specific negative control under investigation.
    jq -e '.passed == false and .traceComplete and (.error | startswith("System.Net.Sockets.SocketException (0x00000030):"))' \
      "$output/managed.json" > /dev/null || return 2
  else
    return "$exit_code"
  fi
  return "$exit_code"
}

build_native control
# Negative-control outcomes are data, including a possible non-reproduction.
# Ordinary CI retains its independent, unsuppressed pinned-bottle regression.
for traced in untraced traced; do
  for iteration in 1 2 3; do
    status=0
    run_probe control "$iteration" "$traced" || status=$?
    test "$status" -le 1
    printf '%s\t%s\t%s\n' "$traced" "$iteration" "$status" >> "$results/control-exits.tsv"
  done
done
status=0
"$RUNNER_TEMP/msquic-control-build/bin/msquicplatformtest" \
  --timeout 120000 --gtest_filter='*DataPath*' \
  --gtest_output="xml:$results/control-datapath.xml" \
  > "$results/control-datapath.log" 2>&1 || status=$?
printf '%s\n' "$status" > "$results/control-datapath.exit"

git -C "$source_dir" apply --check "$patch_file"
git -C "$source_dir" apply "$patch_file"
git -C "$source_dir" diff --check
git -C "$source_dir" diff -- src/platform/datapath_kqueue.c > "$results/applied.patch"
cmp "$patch_file" "$results/applied.patch"
build_native candidate
candidate_failed=0
"$RUNNER_TEMP/msquic-candidate-build/bin/msquicplatformtest" \
  --timeout 120000 --gtest_filter='*DataPath*' \
  --gtest_output="xml:$results/candidate-datapath.xml" \
  2>&1 | tee "$results/candidate-datapath.log" || candidate_failed=1
for traced in untraced traced; do
  for iteration in 1 2 3 4 5; do
    status=0
    run_probe candidate "$iteration" "$traced" || status=$?
    printf '%s\t%s\t%s\n' "$traced" "$iteration" "$status" >> "$results/candidate-exits.tsv"
    if test "$status" -ne 0; then candidate_failed=1; fi
  done
done
export DYLD_FALLBACK_LIBRARY_PATH="$RUNNER_TEMP/msquic-candidate-build/bin"
export EMBEDIO_REQUIRE_QUIC=1
for iteration in 1 2 3 4 5; do
  dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-build \
    --filter 'FullyQualifiedName~QuicRuntimeRebindTest' --minimum-expected-tests 2 \
    --timeout 2m --report-trx --results-directory "$results/connected-$iteration" || candidate_failed=1
done
dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-build \
  --minimum-expected-tests 3400 --timeout 5m --report-trx --coverlet \
  --results-directory "$results/full-suite" || candidate_failed=1
printf '%s\n' "$candidate_failed" > "$results/candidate.exit"
exit "$candidate_failed"
