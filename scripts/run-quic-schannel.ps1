param([string]$Output = 'TestResults/quic-schannel')

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Schannel validation requires Windows.' }
if ([string]::IsNullOrWhiteSpace($env:EMBEDIO_DATAGRAM_PEER_PYTHON)) { throw 'Provision the pinned independent peer first.' }
if (Test-Path -LiteralPath $output) { throw 'Use a fresh Schannel validation output directory.' }
New-Item -ItemType Directory $output | Out-Null
dotnet restore test/EmbedIO.Quic/EmbedIO.Quic.csproj --locked-mode
if ($LASTEXITCODE) { throw 'QUIC host restore failed.' }
dotnet build test/EmbedIO.Quic/EmbedIO.Quic.csproj -c Release --no-restore -p:RunAnalyzers=true
if ($LASTEXITCODE) { throw 'QUIC host build failed.' }
$assembly = 'test/EmbedIO.Quic/bin/Release/net10.0/EmbedIO.Quic.Development.dll'
dotnet $assembly schannel-self-test > "$output/lifetime.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/lifetime.log"; throw 'Schannel lifetime checks failed.' }
foreach ($provider in 'schannel-rsa', 'schannel-ecdsa') {
  & $env:EMBEDIO_DATAGRAM_PEER_PYTHON test/EmbedIO.Quic/verify.py --dotnet dotnet --assembly $assembly --openssl $provider --output "$output/$provider-wire" > "$output/$provider-peer.log" 2>&1
  if ($LASTEXITCODE) { Get-Content "$output/$provider-peer.log"; throw 'Schannel independent peer checks failed.' }
  dotnet $assembly fuzz-native $provider "$output/$provider-public.pem" unused-for-schannel 1729 1000 "$output/$provider-native" > "$output/$provider-native.log" 2>&1
  if ($LASTEXITCODE) { Get-Content "$output/$provider-native.log"; throw 'Schannel native mutation checks failed.' }
}
