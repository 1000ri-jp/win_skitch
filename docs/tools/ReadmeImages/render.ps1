$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$env:DOTNET_CLI_HOME = Join-Path $workspace '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $workspace '.tools\nuget'
$localDotnet = Join-Path $workspace '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
} else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) { throw '.NET 10 SDK is required to render README images.' }
    $dotnet = $command.Source
}
& $dotnet run --project (Join-Path $PSScriptRoot 'ReadmeImages.csproj') --configuration Release `
    -p:TargetPlatformDisplayName=Windows "-p:RestoreConfigFile=$(Join-Path $workspace 'windows\NuGet.Config')" `
    -- (Join-Path $workspace 'docs\images')
if ($LASTEXITCODE -ne 0) { throw "README image rendering failed (exit code $LASTEXITCODE)." }
