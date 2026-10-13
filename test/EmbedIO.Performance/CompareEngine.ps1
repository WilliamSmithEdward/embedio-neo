param(
    [Parameter(Mandatory=$true)][string]$BaselineRunner,
    [Parameter(Mandatory=$true)][string]$CandidateRunner,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [int]$Seconds = 15,
    [int]$Rounds = 3,
    [int]$Concurrency = 16
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ComparisonProcesses.ps1')
$BaselineRunner=(Resolve-Path -LiteralPath $BaselineRunner).Path
$CandidateRunner=(Resolve-Path -LiteralPath $CandidateRunner).Path
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
function Line($process) {
    return $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(90)).GetAwaiter().GetResult()
}
foreach($pipeline in 1,16) {
    foreach($round in 1..$Rounds) {
        foreach($side in 'before','after') {
            $reservation=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
            $reservation.Start()
            $port=$reservation.LocalEndpoint.Port
            $reservation.Stop()
            $url="http://127.0.0.1:$port/"
            $runner=if($side -eq 'before'){$BaselineRunner}else{$CandidateRunner}
            $server=Start-ComparisonProcess $runner @('--benchmark-endpoints','--measure','--url',$url)
            $client=$null
            try {
                if([Environment]::ProcessorCount -ge 8){$server.Process.ProcessorAffinity=[IntPtr]15}
                $greeting=Line $server.Process
                if($greeting -notlike 'Benchmark endpoints:*'){throw "Host failed: $greeting"}
                # The host prints before entering its accept loop; confirm readiness separately.
                $probe=[Net.Http.HttpClient]::new()
                try {
                    $probe.Timeout=[TimeSpan]::FromSeconds(5)
                    $ready=$false
                    for($attempt=0;$attempt -lt 50;$attempt++) {
                        try { if($probe.GetStringAsync($url+'plaintext').GetAwaiter().GetResult() -eq 'Hello, World!'){$ready=$true;break} }
                        catch { Start-Sleep -Milliseconds 100 }
                    }
                    if(!$ready){throw 'Host never became ready.'}
                } finally {$probe.Dispose()}
                $client=Start-ComparisonProcess $CandidateRunner @('--engine-load','--url',($url+'plaintext'),'--seconds',"$Seconds",'--concurrency',"$Concurrency",'--pipeline',"$pipeline")
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
                if(!$client.Process.WaitForExit(10000) -or $client.Process.ExitCode -ne 0){throw 'Client did not exit cleanly.'}
                if(!$server.Process.WaitForExit(10000) -or $server.Process.ExitCode -ne 0){throw 'Server did not exit cleanly.'}
                $result=@{side=$side;round=$round;pipeline=$pipeline;server=$serverResult;client=$clientResult;
                    coreSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path $runner) 'EmbedIO.dll') -Algorithm SHA256).Hash}
                $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory "$side-p$pipeline-r$round.json")
                Write-Output "$side pipeline=$pipeline round=$round requests=$($clientResult.requests) rps=$([math]::Round($clientResult.requestsPerSecond))"
            } finally {
                Stop-ComparisonProcesses @($client,$server) (Join-Path $OutputDirectory 'stderr.log')
            }
        }
    }
}
