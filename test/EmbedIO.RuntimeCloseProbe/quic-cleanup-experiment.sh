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
config_patch="$repo/test/EmbedIO.RuntimeCloseProbe/msquic-kqueue-config.patch"
cp "$config_patch" "$results/config-candidate.patch"
shasum -a 256 "$config_patch" > "$results/config-patch.sha256"
# Correct the optional-feature heuristic identically in both variants. The
# separately verified datagram fixture continues to check actual receive data.
zero_test_patch="$repo/test/EmbedIO.RuntimeCloseProbe/msquic-zero-config-test.patch"
cp "$zero_test_patch" "$results/zero-config-test.patch"
shasum -a 256 "$zero_test_patch" > "$results/zero-config-test.sha256"
git -C "$source_dir" apply --check "$zero_test_patch"
git -C "$source_dir" apply "$zero_test_patch"
git -C "$source_dir" diff --check
git -C "$source_dir" diff -- src/platform/unittest/DataPathTest.cpp > "$results/applied-zero-config-test.patch"
cmp "$zero_test_patch" "$results/applied-zero-config-test.patch"
# Compile the same deterministic lifetime fixture into both variants.
fixture="$repo/test/EmbedIO.RuntimeCloseProbe/kqueue-lifetime-test.inc"
cp "$fixture" "$results/kqueue-lifetime-test.inc"
shasum -a 256 "$fixture" > "$results/lifetime-fixture.sha256"
printf '\n#include "%s"\n' "$fixture" >> "$source_dir/src/platform/unittest/DataPathTest.cpp"
config_fixture="$repo/test/EmbedIO.RuntimeCloseProbe/kqueue-config-test.inc"
cp "$config_fixture" "$results/kqueue-config-test.inc"
shasum -a 256 "$config_fixture" > "$results/config-fixture.sha256"
printf '\n#include "%s"\n' "$config_fixture" >> "$source_dir/src/platform/unittest/DataPathTest.cpp"

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
  local variant="$1" asan="${2:-OFF}"
  local build_dir="$RUNNER_TEMP/msquic-$variant-build"
  local project_include=""
  if test "$asan" = ON; then
    # The pinned source's sanitizer branch uses this command without loading
    # its module. Load the standard module after project() enables C, without
    # changing upstream source or substituting the compiler-flag probe result.
    printf 'include(CheckCCompilerFlag)\n' > "$results/sanitizer-project-include.cmake"
    project_include="$results/sanitizer-project-include.cmake"
  fi
  cmake -S "$source_dir" -B "$build_dir" \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH="$openssl_root" \
    -DQUIC_TLS_LIB=quictls -DQUIC_USE_SYSTEM_LIBCRYPTO=ON \
    -DQUIC_BUILD_TEST=ON -DQUIC_BUILD_TOOLS=OFF -DQUIC_BUILD_PERF=OFF \
    -DQUIC_ENABLE_LOGGING=OFF -DQUIC_ENABLE_ASAN="$asan" -DCMAKE_EXPORT_COMPILE_COMMANDS=ON -DFETCHCONTENT_FULLY_DISCONNECTED=ON \
    -DQUIC_OUTPUT_DIR="$build_dir/bin" "-DCMAKE_PROJECT_INCLUDE=$project_include" 2>&1 | tee "$results/$variant-configure.log"
  cmake --build "$build_dir" --parallel 3 --target msquic msquicplatformtest \
    2>&1 | tee "$results/$variant-build.log"
  cp "$build_dir/CMakeCache.txt" "$results/$variant-CMakeCache.txt"
  cp "$build_dir/compile_commands.json" "$results/$variant-compile_commands.json"
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
# Separate correction for explicitly requested, unsupported raw/XDP map mode.
# Production control source remains unchanged; both variants use the same tests.
git -C "$source_dir" apply --check "$config_patch"
git -C "$source_dir" apply "$config_patch"
git -C "$source_dir" apply --reverse --check "$config_patch"
git -C "$source_dir" diff --check
git -C "$source_dir" diff -- src/platform/datapath_kqueue.c > "$results/combined-candidate.patch"
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
  --minimum-expected-tests 3502 --timeout 5m --report-trx --coverlet \
  --results-directory "$results/full-suite" || candidate_failed=1
# Additional memory-lifetime validation with upstream's sanitizer build.
# The ordinary native suite above retains every genuine failure in final status.
build_native candidate-asan ON
asan_bin="$RUNNER_TEMP/msquic-candidate-asan-build/bin"
otool -L "$asan_bin/msquicplatformtest" > "$results/candidate-asan-test-imports.txt"
asan_status=0
env DYLD_FALLBACK_LIBRARY_PATH="$asan_bin" DYLD_PRINT_LIBRARIES=1 \
  "$asan_bin/msquicplatformtest" --timeout 120000 \
    --gtest_filter='*EmbedIOKqueue*:*UdpData*:*XdpMapMode_InitFailsWithoutRawDatapath:*XdpMapMode_ZeroConfigUsesNormalPath' --gtest_repeat=100 \
    --gtest_shuffle --gtest_random_seed=40591 \
    --gtest_output="xml:$results/candidate-asan-lifetime.xml" \
    > "$results/candidate-asan-lifetime.log" 2> "$results/candidate-asan-loader.log" || asan_status=$?
printf '%s\n' "$asan_status" > "$results/candidate-asan.exit"
if test "$asan_status" -ne 0; then candidate_failed=1; fi
printf '%s\n' "$candidate_failed" > "$results/candidate.exit"
exit "$candidate_failed"
