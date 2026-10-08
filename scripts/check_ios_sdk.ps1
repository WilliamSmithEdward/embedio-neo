param([switch]$Required)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $IsMacOS -or $env:SMOKE_PLATFORM -ne 'ios') {
    throw 'Run the iOS SDK check only on its disposable GitHub macOS runner.'
}
$taskSdkRoot = $env:DOTNET_INSTALL_DIR
if ([string]::IsNullOrWhiteSpace($taskSdkRoot)) { throw 'DOTNET_INSTALL_DIR is required.' }
$taskDotnet = Join-Path $taskSdkRoot 'dotnet'
if (-not (Test-Path -LiteralPath $taskDotnet -PathType Leaf)) {
    if ($Required) { throw 'The pinned SDK installation is missing.' }
    'ready=false' >> $env:GITHUB_OUTPUT
    exit 0
}
$taskVersion = (& $taskDotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $taskVersion -ne '10.0.401') {
    throw "Expected SDK 10.0.401; received $taskVersion."
}
$taskRuntimes = & $taskDotnet --list-runtimes
if ($LASTEXITCODE -ne 0 -or -not ($taskRuntimes -match '^Microsoft\.NETCore\.App 10\.0\.12 \[')) {
    throw 'The pinned Microsoft.NETCore.App 10.0.12 runtime is missing.'
}
"DOTNET_ROOT=$taskSdkRoot" >> $env:GITHUB_ENV
$taskSdkRoot >> $env:GITHUB_PATH
'ready=true' >> $env:GITHUB_OUTPUT
New-Item -ItemType Directory -Force TestResults/maui-https/ios | Out-Null
@{ sdk = $taskVersion; runtime = '10.0.12'; root = $taskSdkRoot; verified = $true } |
    ConvertTo-Json | Set-Content -Encoding utf8 TestResults/maui-https/ios/sdk-setup.json
Write-Output 'Verified the exact SDK 10.0.401 and runtime 10.0.12.'

