param([ValidateSet('build', 'run')][string]$Phase)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -ne 'WilliamSmithEdward/embedio-neo') {
    throw 'This isolation probe is restricted to disposable GitHub runners.'
}
$fixturePath = Join-Path $env:RUNNER_TEMP 'embedio-appcontainer-host'
if ($Phase -eq 'build') {
    dotnet restore test/EmbedIO.AppContainerSmoke/EmbedIO.AppContainerSmoke.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet publish test/EmbedIO.AppContainerSmoke/EmbedIO.AppContainerSmoke.csproj -c Release --no-restore -o $fixturePath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} else {
    & (Join-Path $fixturePath 'EmbedIO.AppContainerSmoke.exe') (Join-Path $env:GITHUB_WORKSPACE 'TestResults/appcontainer')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $report = Get-Content -Raw TestResults/appcontainer/result.json | ConvertFrom-Json
    if (-not $report.passed) { throw 'AppContainer network-isolation smoke failed' }
}
