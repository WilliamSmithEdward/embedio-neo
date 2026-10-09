param(
    [Parameter(Mandatory=$true)][string]$BaselineRunner,
    [Parameter(Mandatory=$true)][string]$CandidateRunner,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [double]$Seconds = 10,
    [int]$Rounds = 3
)
# Alternates baseline and candidate hosts on identical workloads. Both runner
# directories must contain the same harness build and differ only in EmbedIO.dll;
# the client always comes from the candidate directory so it is identical on both sides.
$ErrorActionPreference='Stop'
$BaselineRunner=(Resolve-Path -LiteralPath $BaselineRunner).Path
$CandidateRunner=(Resolve-Path -LiteralPath $CandidateRunner).Path
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$workloads=@(
    @{name='small-1';size=32;fragments=1;connections=1;text=$false},
    @{name='small-16';size=32;fragments=1;connections=16;text=$false},
    @{name='text-1k-4';size=1024;fragments=1;connections=4;text=$true},
    @{name='binary-64k-4';size=65536;fragments=1;connections=4;text=$false},
    @{name='fragmented-64k-4';size=65536;fragments=16;connections=4;text=$false},
    @{name='binary-1m-1';size=1048576;fragments=1;connections=1;text=$false}
)
function Launch([string]$Runner,[string[]]$Options) {
    $info=[Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true
    $info.RedirectStandardOutput=$true
    $info.RedirectStandardError=$true
    $info.ArgumentList.Add($Runner)
    foreach($option in $Options) { $info.ArgumentList.Add($option) }
    $process=[Diagnostics.Process]::Start($info)
    return @{ Process=$process; Errors=$process.StandardError.ReadToEndAsync() }
}
function Line($process) {
    $line=$process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(180)).GetAwaiter().GetResult()
    if($null -eq $line){throw 'Child process ended without output.'}
    return $line
}
foreach($workload in $workloads) {
    foreach($round in 1..$Rounds) {
        foreach($side in 'before','after') {
            $reservation=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
            $reservation.Start()
            $port=$reservation.LocalEndpoint.Port
            $reservation.Stop()
            $url="http://127.0.0.1:$port/"
            $runner=if($side -eq 'before'){$BaselineRunner}else{$CandidateRunner}
            $server=Launch $runner @('--websocket-host','--url',$url)
            if([Environment]::ProcessorCount -ge 8){$server.Process.ProcessorAffinity=[IntPtr]15}
            $client=$null
            try {
                if((Line $server.Process) -notlike 'HOST *'){throw 'Host failed to start.'}
                $options=@('--websocket-load','--url',$url,'--size',"$($workload.size)",'--fragments',"$($workload.fragments)",
                    '--connections',"$($workload.connections)",'--seconds',"$Seconds")
                if($workload.text){$options+='--text'}
                $client=Launch $CandidateRunner $options
                if([Environment]::ProcessorCount -ge 8){$client.Process.ProcessorAffinity=[IntPtr]240}
                if((Line $client.Process) -ne 'READY'){throw 'Client warmup failed.'}
                $server.Process.StandardInput.WriteLine('start')
                if((Line $server.Process) -ne 'MEASURING'){throw 'Server measurement handshake failed.'}
                $client.Process.StandardInput.WriteLine('start')
                if((Line $client.Process) -ne 'DONE'){throw 'Client measurement failed.'}
                $server.Process.StandardInput.WriteLine('stop')
                $serverResult=(Line $server.Process) | ConvertFrom-Json
                $client.Process.StandardInput.WriteLine('report')
                $clientResult=(Line $client.Process) | ConvertFrom-Json
                if((Line $client.Process) -ne 'CLOSED'){throw 'Client close handshakes failed.'}
                $server.Process.StandardInput.WriteLine('closed')
                $closedResult=(Line $server.Process) | ConvertFrom-Json
                if(!$client.Process.WaitForExit(10000) -or $client.Process.ExitCode -ne 0){throw 'Client did not exit cleanly.'}
                if(!$server.Process.WaitForExit(10000) -or $server.Process.ExitCode -ne 0){throw 'Server did not exit cleanly.'}
                $result=@{side=$side;round=$round;workload=$workload.name;server=$serverResult;client=$clientResult;closed=$closedResult}
                $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory "$($workload.name)-$side-r$round.json")
                $perMessage=[math]::Round($serverResult.allocatedBytes / [math]::Max(1,$clientResult.messages))
                Write-Output "$($workload.name) $side r$round msg/s=$([math]::Round($clientResult.messagesPerSecond)) p50=$([math]::Round($clientResult.p50Microseconds,1))us p99=$([math]::Round($clientResult.p99Microseconds,1))us alloc/msg=$perMessage"
            } finally {
                foreach($child in @($client,$server)) {
                    if($null -eq $child){continue}
                    if(!$child.Process.HasExited){$child.Process.Kill($true);$child.Process.WaitForExit()}
                    $errorText=$child.Errors.GetAwaiter().GetResult()
                    if($errorText){$errorText | Add-Content -LiteralPath (Join-Path $OutputDirectory 'stderr.log')}
                    $child.Process.Dispose()
                }
            }
        }
    }
}
