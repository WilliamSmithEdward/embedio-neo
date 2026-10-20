param(
    [ValidateRange(1, 1800)][int]$Seconds = 60,
    [ValidateRange(1, 2147483647)][int]$Seed = 1729
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'test/EmbedIO.SharpFuzz/EmbedIO.SharpFuzz.csproj'
# A fresh directory prevents double instrumentation and keeps ordinary build outputs intact.
$output = Join-Path $repo ('TestResults/sharpfuzz/' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $output 'publish'
$tools = Join-Path $output 'tools'
$corpus = Join-Path $output 'corpus'
$crashes = Join-Path $output 'crashes'
New-Item -ItemType Directory -Force $tools, $corpus, $crashes | Out-Null

if ($IsWindows -and [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64') {
    $asset = 'libfuzzer-dotnet-windows.exe'
    $hash = '17AF5B3F6FF4D2C57B44B9A35C13051B570EB66F0557D00015DF3832709050BF'
} elseif ($IsLinux -and [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64') {
    $asset = 'libfuzzer-dotnet-ubuntu'
    $hash = 'C2C2A90D94C409A4AF339A0D4F244E0442C5A5D249BE0F1252BA07871F285958'
} else {
    throw 'This campaign supports Windows x64 and Ubuntu x64.'
}
$driver = Join-Path $tools $asset
$release = 'v2025.05.02.0904'
Invoke-WebRequest "https://github.com/Metalnem/libfuzzer-dotnet/releases/download/$release/$asset" -OutFile $driver
if ((Get-FileHash $driver -Algorithm SHA256).Hash -ne $hash) { throw 'Driver checksum mismatch.' }
if ($IsLinux) {
    & chmod +x $driver
    if ($LASTEXITCODE) { throw 'Could not make driver executable.' }
}

@{ seed = $Seed; seconds = $Seconds; maxLength = 4096; sharpFuzz = '2.3.0'; driver = $release; driverSha256 = $hash; source = (git -C $repo rev-parse HEAD); workingTreeChanges = @(git -C $repo status --porcelain) } |
    ConvertTo-Json | Set-Content (Join-Path $output 'campaign.json')
dotnet restore $project --locked-mode 2>&1 | Tee-Object (Join-Path $output 'restore.log')
if ($LASTEXITCODE) { throw 'Locked restore failed.' }
dotnet publish $project -c Release --no-restore -o $publish -m:1 2>&1 | Tee-Object (Join-Path $output 'build.log')
if ($LASTEXITCODE) { throw 'Publish failed.' }
dotnet tool install SharpFuzz.CommandLine --version 2.3.0 --tool-path $tools 2>&1 | Tee-Object (Join-Path $output 'tool.log')
if ($LASTEXITCODE) { throw 'Instrumenter installation failed.' }

$quicAssembly = Join-Path $publish 'EmbedIO.Quic.dll'
$quicOrdinary = Join-Path $repo 'test/EmbedIO.Quic/bin/Release/net10.0/EmbedIO.Quic.dll'
$quicOrdinaryHash = (Get-FileHash $quicOrdinary).Hash
$quicOriginalHash = (Get-FileHash $quicAssembly).Hash
$assembly = Join-Path $publish 'EmbedIO.dll'
$ordinaryAssembly = Join-Path $repo 'src/EmbedIO/bin/Release/net10.0/EmbedIO.dll'
$ordinaryHash = (Get-FileHash $ordinaryAssembly).Hash
$originalHash = (Get-FileHash $assembly).Hash
$priorRollForward = $env:DOTNET_ROLL_FORWARD
$priorPrintTypes = $env:SHARPFUZZ_PRINT_INSTRUMENTED_TYPES
try {
    # The instrumenter targets net8; the repository pins and installs net10.
    $env:DOTNET_ROLL_FORWARD = 'Major'
    $env:SHARPFUZZ_PRINT_INSTRUMENTED_TYPES = '1'
    & (Join-Path $tools 'sharpfuzz') $assembly EmbedIO.Utilities.UrlPath EmbedIO.Utilities.UrlEncodedDataParser EmbedIO.MultipartReader |
        Tee-Object (Join-Path $output 'instrumentation.log')
    if ($LASTEXITCODE) { throw 'Instrumentation failed.' }
    & (Join-Path $tools 'sharpfuzz') $quicAssembly EmbedIO.Quic.Wire EmbedIO.Quic.PacketHeader EmbedIO.Quic.AckRanges EmbedIO.Quic.PacketNumberWindow EmbedIO.Quic.PacketKeys EmbedIO.Quic.Reassembly EmbedIO.Quic.StreamReassembly EmbedIO.Quic.Recovery EmbedIO.Quic.ConnectionLifetime EmbedIO.Quic.ConnectionStreams EmbedIO.Quic.WebTransportApplication EmbedIO.Net.Internal |
        Tee-Object (Join-Path $output 'quic-instrumentation.log')
    if ($LASTEXITCODE) { throw 'QUIC instrumentation failed.' }
} finally {
    $env:DOTNET_ROLL_FORWARD = $priorRollForward
    $env:SHARPFUZZ_PRINT_INSTRUMENTED_TYPES = $priorPrintTypes
}
$instrumentation = Get-Content (Join-Path $output 'instrumentation.log') -Raw
if ($instrumentation -notmatch 'EmbedIO\.MultipartReader/<ReadBodyCoreAsync>' -or
    $instrumentation -notmatch 'EmbedIO\.MultipartReader/<ReadHeadersAsync>') {
    throw 'Multipart async parser state machines were not instrumented.'
}
if ((Get-FileHash $assembly).Hash -eq $originalHash) { throw 'Instrumentation did not change the copied assembly.' }
if ((Get-FileHash $ordinaryAssembly).Hash -ne $ordinaryHash) { throw 'Instrumentation modified the ordinary build output.' }

$quicInstrumentation = Get-Content (Join-Path $output 'quic-instrumentation.log') -Raw
foreach ($type in @('PacketHeader', 'AckRanges', 'PacketNumberWindow', 'PacketKeys', 'StreamReassembly', 'Recovery', 'ConnectionLifetime', 'ConnectionStreams', 'WebTransportApplication')) {
    if ($quicInstrumentation -notmatch ('EmbedIO\.Quic\.' + $type)) { throw "QUIC type was not instrumented: $type" }
}
if ((Get-FileHash $quicAssembly).Hash -eq $quicOriginalHash) { throw 'QUIC instrumentation did not change the copied assembly.' }
if ((Get-FileHash $quicOrdinary).Hash -ne $quicOrdinaryHash) { throw 'QUIC instrumentation modified the ordinary assembly.' }
$harness = Join-Path $publish 'EmbedIO.SharpFuzz.dll'
$seeds = Get-ChildItem (Join-Path $repo 'test/EmbedIO.SharpFuzz/corpus') -File | Sort-Object Name
foreach ($file in $seeds) {
    dotnet $harness $file.FullName
    if ($LASTEXITCODE) { throw "Seed replay failed: $($file.Name)" }
    Copy-Item $file.FullName $corpus
}
"Replayed $($seeds.Count) seeds." | Set-Content (Join-Path $output 'replay.log')
$log = Join-Path $output 'campaign.log'
& $driver "-max_total_time=$Seconds" -timeout=10 -max_len=4096 "-seed=$Seed" -print_final_stats=1 "-artifact_prefix=$crashes/" --target_path=dotnet "--target_arg=$harness" $corpus 2>&1 | Tee-Object $log
$campaignExit = $LASTEXITCODE
if ($campaignExit) { throw "Campaign failed ($campaignExit). Reproducer files: $crashes" }
$evidence = Get-Content $log -Raw
if ($evidence -notmatch '65536 Extra Counters' -or $evidence -notmatch 'DONE\s+cov:\s*\d+\s+ft:\s*([1-9]\d+)') {
    throw 'Campaign did not report managed coverage feedback and a completed run.'
}
Write-Output "Campaign evidence: $output"
