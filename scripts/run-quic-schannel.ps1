param([string]$Output = 'TestResults/quic-schannel')

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Schannel validation requires Windows.' }
if ([string]::IsNullOrWhiteSpace($env:EMBEDIO_DATAGRAM_PEER_PYTHON)) { throw 'Provision the pinned independent peer first.' }
if (Test-Path -LiteralPath $output) { throw 'Use a fresh Schannel validation output directory.' }
New-Item -ItemType Directory $output | Out-Null
# Read-only host policy evidence; unavailable diagnostics never replace the TLS gate.
$policy = [ordered]@{ os = [Environment]::OSVersion.VersionString }
try { $policy.curves = [string[]](Get-TlsEccCurve -ErrorAction Stop) }
catch { $policy.curvesError = $_.Exception.Message }
try {
  $fipsPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa\FipsAlgorithmPolicy'
  $policy.legacyFipsEnabled = Get-ItemPropertyValue -LiteralPath $fipsPath -Name Enabled -ErrorAction Stop
}
catch { $policy.legacyFipsError = $_.Exception.Message }
$policy | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath "$output/host-policy.json" -Encoding utf8
if ($policy.curvesError -or $policy.legacyFipsError) { throw 'TLS group policy could not be read; retain the host-policy report.' }
if ($policy.curves -notcontains 'NistP256') { throw 'The native acceptance fixture requires enabled P-256.' }
$x25519 = if ($policy.curves -contains 'curve25519' -and $policy.legacyFipsEnabled -eq 0) { 'enabled' } else { 'disabled' }
dotnet restore test/EmbedIO.Quic/EmbedIO.Quic.csproj --locked-mode
if ($LASTEXITCODE) { throw 'QUIC host restore failed.' }
dotnet build test/EmbedIO.Quic/EmbedIO.Quic.csproj -c Release --no-restore -p:RunAnalyzers=true
if ($LASTEXITCODE) { throw 'QUIC host build failed.' }
$assembly = 'test/EmbedIO.Quic/bin/Release/net10.0/EmbedIO.Quic.Development.dll'
& $env:EMBEDIO_DATAGRAM_PEER_PYTHON -m unittest discover -s test/EmbedIO.Quic -p 'test_*peer.py' > "$output/http-peer.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/http-peer.log"; throw 'Independent HTTP/3 peer regression checks failed.' }
dotnet $assembly schannel-self-test > "$output/lifetime.log" 2>&1
if ($LASTEXITCODE) {
  Get-Content "$output/lifetime.log"
  # Preserve the original hard failure; diagnostics cannot turn it into a pass.
  dotnet $assembly schannel-contract-probe $x25519 > "$output/first-flight-contracts.log" 2>&1
  Get-Content "$output/first-flight-contracts.log"
  throw 'Schannel lifetime checks failed.'
}
# Exercise enabled and disabled groups against the captured host policy.
dotnet $assembly schannel-contract-probe $x25519 > "$output/first-flight-contracts.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/first-flight-contracts.log"; throw 'Schannel group policy checks failed.' }
foreach ($provider in 'schannel-rsa', 'schannel-ecdsa') {
  & $env:EMBEDIO_DATAGRAM_PEER_PYTHON test/EmbedIO.Quic/verify.py --dotnet dotnet --assembly $assembly --openssl $provider --output "$output/$provider-wire" > "$output/$provider-peer.log" 2>&1
  if ($LASTEXITCODE) { Get-Content "$output/$provider-peer.log"; throw 'Schannel independent peer checks failed.' }
  dotnet $assembly fuzz-native $provider "$output/$provider-public.pem" unused-for-schannel 1729 1000 "$output/$provider-native" > "$output/$provider-native.log" 2>&1
  if ($LASTEXITCODE) { Get-Content "$output/$provider-native.log"; throw 'Schannel native mutation checks failed.' }
}

& $env:EMBEDIO_DATAGRAM_PEER_PYTHON test/EmbedIO.Quic/verify_sni.py --dotnet dotnet --assembly $assembly --output "$output/selection" > "$output/selection.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/selection.log"; throw 'Schannel certificate-selection checks failed.' }

& $env:EMBEDIO_DATAGRAM_PEER_PYTHON test/EmbedIO.Quic/verify_client_certificate.py --profile ci --dotnet dotnet --assembly $assembly --output "$output/client-certificate" > "$output/client-certificate.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/client-certificate.log"; throw 'Schannel client-certificate policy checks failed.' }
& $env:EMBEDIO_DATAGRAM_PEER_PYTHON test/EmbedIO.Quic/fuzz_client_certificate.py --dotnet dotnet --assembly $assembly --cases 16 --seed 1729 --output "$output/client-certificate-mutations" > "$output/client-certificate-mutations.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/client-certificate-mutations.log"; throw 'Schannel client-certificate mutations failed.' }

& $env:EMBEDIO_DATAGRAM_PEER_PYTHON test/EmbedIO.Quic/verify_cipher_suites.py --dotnet dotnet --assembly $assembly --schannel --vectors 96 --output "$output/cipher-suites" > "$output/cipher-suites.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/cipher-suites.log"; throw 'Schannel cipher-suite checks failed.' }

& $env:EMBEDIO_DATAGRAM_PEER_PYTHON test/EmbedIO.Quic/verify_peer_connection_ids.py --dotnet dotnet --assembly $assembly --output "$output/peer-connection-ids" > "$output/peer-connection-ids.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/peer-connection-ids.log"; throw 'Schannel peer connection-ID checks failed.' }

& $env:EMBEDIO_DATAGRAM_PEER_PYTHON test/EmbedIO.Quic/verify_stateless_reset.py --dotnet dotnet --assembly $assembly --output "$output/stateless-reset" > "$output/stateless-reset.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/stateless-reset.log"; throw 'Schannel stateless-reset checks failed.' }

& $env:EMBEDIO_DATAGRAM_PEER_PYTHON test/EmbedIO.Quic/verify_local_connection_ids.py --dotnet dotnet --assembly $assembly --output "$output/local-connection-ids" > "$output/local-connection-ids.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/local-connection-ids.log"; throw 'Schannel local connection-ID checks failed.' }

& $env:EMBEDIO_DATAGRAM_PEER_PYTHON test/EmbedIO.Quic/verify_path_controls.py --dotnet dotnet --assembly $assembly --output "$output/path-controls" > "$output/path-controls.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/path-controls.log"; throw 'Schannel path-control checks failed.' }

& $env:EMBEDIO_DATAGRAM_PEER_PYTHON test/EmbedIO.Quic/verify_path_validation.py --dotnet dotnet --assembly $assembly --output "$output/path-validation" > "$output/path-validation.log" 2>&1
if ($LASTEXITCODE) { Get-Content "$output/path-validation.log"; throw 'Schannel path-validation checks failed.' }
