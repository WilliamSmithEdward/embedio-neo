param([ValidateSet('install', 'build', 'run')][string]$Stage)

$ErrorActionPreference = 'Stop'
switch ($Stage) {
    'install' {
        dotnet workload install $env:SMOKE_WORKLOAD --version 10.0.401
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    'build' {
        New-Item -ItemType Directory -Force TestResults/maui-https | Out-Null
        dotnet restore test/EmbedIO.HttpsCertificates/EmbedIO.HttpsCertificates.csproj --locked-mode
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet run --project test/EmbedIO.HttpsCertificates/EmbedIO.HttpsCertificates.csproj --no-restore -- TestResults/maui-https/certificates
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $restoreMode = if ($env:UPDATE_LOCKS -eq 'true') { '--force-evaluate' } else { '--locked-mode' }
        dotnet restore test/EmbedIO.MauiHttpsSmoke/EmbedIO.MauiHttpsSmoke.csproj "-p:SmokePlatform=$env:SMOKE_PLATFORM" $restoreMode
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet build test/EmbedIO.MauiHttpsSmoke/EmbedIO.MauiHttpsSmoke.csproj "-p:SmokePlatform=$env:SMOKE_PLATFORM" -c Debug --no-restore -p:RunAnalyzers=true
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    'run' {
        $python = if ($IsWindows) { 'python' } else { 'python3' }
        & $python scripts/run_maui_https_smoke.py $env:SMOKE_PLATFORM
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
