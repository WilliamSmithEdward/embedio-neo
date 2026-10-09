#!/usr/bin/env bash
# Test-only experiment. Never installs or adds the candidate to production packages.
set -euo pipefail
test "$(uname -s)" = Darwin
case "$(uname -m)" in
  arm64) native_rid=osx-arm64 ;;
  x86_64) native_rid=osx-x64 ;;
  *) echo "Unsupported native architecture." >&2; exit 2 ;;
esac
if test -n "${EMBEDIO_EXPECT_NATIVE_RID:-}"; then test "$native_rid" = "$EMBEDIO_EXPECT_NATIVE_RID"; fi
repo="$PWD"
self_contained="${EMBEDIO_QUIC_SELF_CONTAINED:-0}"
case "$self_contained" in 0|1) ;; *) echo "Invalid self-contained mode." >&2; exit 2 ;; esac
tls_backend="${EMBEDIO_QUIC_TLS_BACKEND:-quictls}"
case "$tls_backend" in quictls|openssl) ;; *) echo "Invalid TLS backend." >&2; exit 2 ;; esac
if test "$tls_backend" = openssl; then test "$self_contained" = 1; fi
command -v jq
results="$repo/TestResults/quic-cleanup-experiment"
source_dir="$RUNNER_TEMP/msquic-cleanup-source"
revision=819ab74f851ee168504cbc392ec32e7bed1d82e9
mkdir -p "$results"
# Intel's first full runs reached the execution deadline with about half the
# suite completed. Retain the entire floor and all assertions; budget enough
# time for the same coverage-instrumented suite on this slower runner.
full_suite_timeout=5m
if test "$native_rid" = osx-x64; then full_suite_timeout=15m; fi
printf '%s\n' "$full_suite_timeout" > "$results/full-suite-timeout.txt"
test ! -e "$source_dir"
fetch_reviewed_source() {
  local directory="$1" label="$2" ref="$3" attempt status log
  for attempt in 1 2 3; do
    log="$results/$label-fetch-$attempt.log"
    status=0
    git -C "$directory" -c maintenance.auto=false -c gc.auto=0 fetch --depth 1 origin "$ref" > "$log" 2>&1 || status=$?
    cat "$log"
    if test "$status" -eq 0; then return 0; fi
    # Retain every attempt and retry only the observed shallow-state race.
    # Caller still verifies the immutable commit/tag/signature after success.
    if ! grep -Fq 'fatal: shallow file has changed since we read it' "$log"; then return "$status"; fi
    if test "$attempt" -lt 3; then sleep 2; fi
  done
  return "$status"
}

git init "$source_dir"
git -C "$source_dir" remote add origin https://github.com/microsoft/msquic.git
fetch_reviewed_source "$source_dir" msquic "$revision"
git -C "$source_dir" checkout --detach FETCH_HEAD
test "$(git -C "$source_dir" rev-parse HEAD)" = "$revision"
git -C "$source_dir" submodule update --init --depth 1 "submodules/$tls_backend" submodules/googletest
crypto_revision=ff36838bb69801cad56823159a036977bcbe5c75
crypto_root="$source_dir/submodules/$tls_backend"
if test "$tls_backend" = openssl; then
  # OpenSSL 3.5.9 LTS, released 2026-09-29. Pin the peeled release commit.
  crypto_revision=45e844fa2a14ec92d146bd8f5778ac130b6625fb
  fetch_reviewed_source "$crypto_root" openssl refs/tags/openssl-3.5.9
  test "$(git -C "$crypto_root" rev-parse FETCH_HEAD)" = d0ca66a1abe52545f14eca635c648932fcde5615
  test "$(git -C "$crypto_root" rev-parse 'FETCH_HEAD^{}')" = "$crypto_revision"
  # GnuPG probes its agent socket even during public-key import. Darwin's
  # Unix-socket path limit requires a short job-local keyring directory.
  key_root="$RUNNER_TEMP/openssl-keyring"
  mkdir -m 700 "$key_root"
  curl --fail --location --retry 3 https://www.openssl-library.org/source/pubkeys.asc -o "$results/openssl-pubkeys.asc"
  gpg --version > "$results/gpg-version.txt"
  gpg --no-options --no-autostart --homedir "$key_root" --batch --import "$results/openssl-pubkeys.asc" \
    > "$results/openssl-key-import.log" 2>&1
  printf 'no-autostart\n' > "$key_root/gpg.conf"
  GNUPGHOME="$key_root" git -C "$crypto_root" verify-tag --raw FETCH_HEAD \
    > "$results/openssl-tag-signature.log" 2>&1
  grep -E '^\[GNUPG:\] VALIDSIG .* B146647E45A7B33947AB226B2A2C87D161692D40$' "$results/openssl-tag-signature.log"
  git -C "$crypto_root" checkout --detach "$crypto_revision"
  grep -Fx 'MAJOR=3' "$crypto_root/VERSION.dat"
  grep -Fx 'MINOR=5' "$crypto_root/VERSION.dat"
  grep -Fx 'PATCH=9' "$crypto_root/VERSION.dat"
fi
test "$(git -C "$crypto_root" rev-parse HEAD)" = "$crypto_revision"
test "$(git -C "$source_dir/submodules/googletest" rev-parse HEAD)" = fa005b296f90faec4f352d7ab382287bf6548c8d
git -C "$source_dir" submodule status > "$results/submodules.txt"
git -C "$source_dir" rev-parse HEAD > "$results/source.txt"
clang --version > "$results/compiler.txt"
cmake --version > "$results/cmake.txt"
system_crypto=ON
openssl_root=""
if test "$self_contained" = 1; then
  system_crypto=OFF
  cp "$crypto_root/VERSION.dat" "$results/crypto-version.txt"
else
  brew list --versions openssl@3 > "$results/openssl.txt"
  openssl_root="$(brew --prefix openssl@3)"
  "$openssl_root/bin/openssl" version -a >> "$results/openssl.txt"
  shasum -a 256 "$openssl_root/lib/libcrypto.3.dylib" >> "$results/openssl.txt"
fi
printf '%s\n' "$self_contained" > "$results/self-contained-mode.txt"
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
  local variant="$1" asan="${2:-OFF}" tests="${3:-ON}"
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
    "-DQUIC_TLS_LIB=$tls_backend" "-DQUIC_USE_SYSTEM_LIBCRYPTO=$system_crypto" \
    "-DQUIC_BUILD_TEST=$tests" -DQUIC_BUILD_TOOLS=OFF -DQUIC_BUILD_PERF=OFF \
    -DQUIC_ENABLE_LOGGING=OFF -DQUIC_ENABLE_ASAN="$asan" -DCMAKE_EXPORT_COMPILE_COMMANDS=ON -DFETCHCONTENT_FULLY_DISCONNECTED=ON \
    -DQUIC_OUTPUT_DIR="$build_dir/bin" "-DCMAKE_PROJECT_INCLUDE=$project_include" 2>&1 | tee "$results/$variant-configure.log"
  if test "$tests" = ON; then
    cmake --build "$build_dir" --parallel 3 --target msquic msquicplatformtest \
      2>&1 | tee "$results/$variant-build.log"
  else
    cmake --build "$build_dir" --parallel 3 --target msquic \
      2>&1 | tee "$results/$variant-build.log"
  fi
  cp "$build_dir/CMakeCache.txt" "$results/$variant-CMakeCache.txt"
  cp "$build_dir/compile_commands.json" "$results/$variant-compile_commands.json"
  codesign --force --sign - "$build_dir/bin/libmsquic.2.6.2.dylib"
  shasum -a 256 "$build_dir/bin/libmsquic.2.6.2.dylib" > "$results/$variant-library.sha256"
  otool -L "$build_dir/bin/libmsquic.2.6.2.dylib" > "$results/$variant-imports.txt"
  if test "$self_contained" = 1 && test "$asan" = OFF; then
    # A distributable candidate must not retain build/Homebrew crypto paths.
    awk 'NR > 1 { print $1 }' "$results/$variant-imports.txt" > "$results/$variant-dependencies.txt"
    if grep -Ev '^(@rpath/libmsquic\.2\.dylib$|/usr/lib/|/System/Library/)' "$results/$variant-dependencies.txt"; then return 2; fi
    if grep -Ei '(libcrypto|libssl)' "$results/$variant-dependencies.txt"; then return 2; fi
    # MsQuic's Darwin export list hides bundled crypto from other libraries.
    nm -gjU "$build_dir/bin/libmsquic.2.6.2.dylib" | LC_ALL=C sort > "$results/$variant-exports.txt"
    LC_ALL=C sort "$source_dir/src/bin/darwin/exports.txt" > "$results/expected-exports.txt"
    cmp "$results/expected-exports.txt" "$results/$variant-exports.txt"
  fi
}

run_probe() {
  local variant="$1" iteration="$2" traced="$3"
  local output="$results/$variant-$traced-$iteration"
  local library_dir="${4:-$RUNNER_TEMP/msquic-$variant-build/bin}"
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
if test "$self_contained" = 1; then
  staged_variant=candidate
  staged_tests=true
  if test "$tls_backend" = openssl; then
    build_native candidate-production OFF OFF
    staged_variant=candidate-production
    staged_tests=false
  fi
  # Stage only the review artifact: no NuGet asset or loader override is installed.
  stage="$results/self-contained-candidate"
  native="$stage/runtimes/$native_rid/native"
  mkdir -p "$native" "$stage/licenses" "$stage/source"
  cp "$RUNNER_TEMP/msquic-$staged_variant-build/bin/libmsquic.2.6.2.dylib" "$native/libmsquic.2.6.2.dylib"
  ln -s libmsquic.2.6.2.dylib "$native/libmsquic.2.dylib"
  ln -s libmsquic.2.dylib "$native/libmsquic.dylib"
  cp "$source_dir/LICENSE" "$stage/licenses/MsQuic-LICENSE"
  cp "$source_dir/THIRD-PARTY-NOTICES" "$stage/licenses/MsQuic-THIRD-PARTY-NOTICES"
  cp "$crypto_root/LICENSE.txt" "$stage/licenses/$tls_backend-LICENSE.txt"
  cp "$patch_file" "$config_patch" "$stage/source/"
  if test "$tls_backend" = openssl; then
    cp "$results/openssl-tag-signature.log" "$results/crypto-version.txt" "$stage/source/"
  fi
  jq -n --arg source "$revision" --arg crypto "$crypto_revision" --arg backend "$tls_backend" --argjson buildTests "$staged_tests" \
    --arg rid "$native_rid" --arg hash "$(shasum -a 256 "$native/libmsquic.2.6.2.dylib" | awk '{print $1}')" \
    '{artifact:"test-only self-contained candidate",rid:$rid,msquicSource:$source,cryptoSource:$crypto,tlsBackend:$backend,buildTests:$buildTests,sha256:$hash,productionInstalled:false}' \
    > "$stage/build-receipt.json"
  status=0
  run_probe candidate-relocated 1 untraced "$native" || status=$?
  printf '%s\n' "$status" > "$results/candidate-relocated.exit"
  if test "$status" -ne 0; then candidate_failed=1; fi
  if test "$tls_backend" = openssl; then
    bash "$repo/test/EmbedIO.NativeDependencyProbe/deployment-probe.sh" "$stage" "$results/native-deployment" || candidate_failed=1
  fi
fi
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
  --minimum-expected-tests 4341 --timeout "$full_suite_timeout" --report-trx --coverlet \
  --results-directory "$results/full-suite" || candidate_failed=1
if test "$self_contained" = 1 && test "$tls_backend" = openssl; then
  # Exercise actual managed protocol/application behavior on the tests-disabled library.
  env DYLD_FALLBACK_LIBRARY_PATH="$RUNNER_TEMP/msquic-candidate-production-build/bin" \
    EMBEDIO_EXPECT_QUIC_LIBRARY_ROOT="$RUNNER_TEMP/msquic-candidate-production-build/bin" \
    EMBEDIO_EXPECT_QUIC_LIBRARY_SHA256="$(jq -r .sha256 "$stage/build-receipt.json")" \
    EMBEDIO_QUIC_LIBRARY_EVIDENCE="$results/production-loaded-library.json" \
    dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-build \
    --minimum-expected-tests 4341 --timeout "$full_suite_timeout" --report-trx --coverlet \
    --results-directory "$results/production-full-suite" || candidate_failed=1
  jq -e '.verified and (.sha256 | length == 64)' "$results/production-loaded-library.json" > /dev/null || candidate_failed=1
fi
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
