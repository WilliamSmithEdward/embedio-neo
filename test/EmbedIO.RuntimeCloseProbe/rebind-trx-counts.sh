#!/usr/bin/env bash
# Prints "total passed failed defect" for one TRX file: every result, the passes,
# the failures, and the failures that are the known macOS MsQuic rebind defect:
# QuicRuntimeRebindTest stopping at bind with AddressAlreadyInUse. Uses xmllint.
# Callers decide what to tolerate; see docs/project/http-engine.md.
set -euo pipefail
trx="$1"
test -f "$trx"
result="//*[local-name()='UnitTestResult']"
raw_rebind="@testId = //*[local-name()='UnitTest'][*[local-name()='TestMethod'][@className='EmbedIO.Tests.QuicRuntimeRebindTest']]/@id"
at_bind="*[local-name()='Output']/*[local-name()='StdOut'][contains(., 'stage=bind,')]"
in_use="*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message'][contains(., 'SocketErrorCode: AddressAlreadyInUse')]"
count() { xmllint --xpath "count($result$1)" "$trx"; }
printf '%s %s %s %s\n' "$(count '')" "$(count "[@outcome='Passed']")" "$(count "[@outcome='Failed']")" \
  "$(count "[@outcome='Failed'][$raw_rebind][$at_bind][$in_use]")"
