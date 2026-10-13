# Offline ownership checks: the fake processes never launch or access the OS.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../test/EmbedIO.Performance/ComparisonProcesses.ps1')
function Fake-Process([bool]$FailsToExit=$false) {
    $fake=[pscustomobject]@{HasExited=$false;Killed=$false;Disposed=$false;FailsToExit=$FailsToExit;WaitMilliseconds=0}
    $fake | Add-Member ScriptMethod Kill { param($tree) $this.Killed=$tree }
    $fake | Add-Member ScriptMethod WaitForExit { param($milliseconds) $this.WaitMilliseconds=$milliseconds; return !$this.FailsToExit }
    $fake | Add-Member ScriptMethod Dispose { $this.Disposed=$true }
    return $fake
}
function Child($process, $errors=[Threading.Tasks.Task]::FromResult[string]('')) {return @{Process=$process;Errors=$errors}}
$first=Fake-Process $true
$second=Fake-Process
$failed=$false
try { Stop-ComparisonProcesses @((Child $first),(Child $second)) $null }
catch [AggregateException] { $failed=$true }
if(!$failed -or !$first.Disposed -or !$second.Disposed -or !$second.Killed){throw 'A failed child cleanup skipped sibling ownership.'}
if($first.WaitMilliseconds -ne 10000 -or $second.WaitMilliseconds -ne 10000){throw 'Cleanup did not use its bounded wait.'}
$first=Fake-Process
$second=Fake-Process
$failed=$false
$stderr=[Threading.Tasks.Task]::FromException[string]([IO.IOException]::new('synthetic stderr failure'))
try { Stop-ComparisonProcesses @((Child $first $stderr),(Child $second)) $null }
catch [AggregateException] { $failed=$true }
if(!$failed -or !$first.Disposed -or !$second.Disposed -or !$second.Killed){throw 'A failed stderr read skipped sibling ownership.'}
$exited=Fake-Process
$exited.HasExited=$true
Stop-ComparisonProcesses @($null,(Child $exited)) $null
if($exited.Killed -or !$exited.Disposed){throw 'Already-exited child cleanup changed termination semantics.'}
Write-Output 'PASS: three offline comparison-child ownership controls.'
