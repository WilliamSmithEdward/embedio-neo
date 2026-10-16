param(
    [ValidateRange(1, 1800)][int]$Seconds = 60,
    [ValidateRange(1, 2147483647)][int]$Seed = 1729
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo ('TestResults/afl/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $output | Out-Null
$tag = 'embedio-afl-tests:5.03c'
docker build --platform linux/amd64 --file (Join-Path $repo 'test/EmbedIO.SharpFuzz/afl.Dockerfile') --tag $tag (Join-Path $repo 'test/EmbedIO.SharpFuzz') 2>&1 | Tee-Object (Join-Path $output 'image.log')
if ($LASTEXITCODE) { throw 'AFL++ image build failed.' }
git -C $repo rev-parse HEAD | Set-Content (Join-Path $output 'source.txt')
git -C $repo status --porcelain | Set-Content (Join-Path $output 'working-tree-changes.txt')
docker run --rm --platform linux/amd64 --cpus 1 --memory 4g --mount "type=bind,source=$repo,target=/input,readonly" --mount "type=bind,source=$output,target=/results" --env "CAMPAIGN_SECONDS=$Seconds" --env "CAMPAIGN_SEED=$Seed" $tag bash /input/scripts/run-afl-container.sh 2>&1 | Tee-Object (Join-Path $output 'console.log')
if ($LASTEXITCODE) { throw "AFL++ validation failed. Evidence: $output" }
Write-Output "AFL++ evidence: $output"
