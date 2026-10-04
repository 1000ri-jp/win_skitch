param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$env:DOTNET_CLI_HOME = Join-Path $workspace '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $workspace '.tools\nuget'
$localDotnet = Join-Path $workspace '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
} else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) { throw '.NET 10 SDK is required to run tests.' }
    $dotnet = $command.Source
}

$project = Join-Path $PSScriptRoot 'WinSkitch.Tests.csproj'
$output = Join-Path $workspace 'artifacts\qa'
# The global property avoids SDK enumeration of unrelated user-profile directories.
& $dotnet run --project $project --configuration $Configuration -p:TargetPlatformDisplayName=Windows `
    "-p:RestoreConfigFile=$(Join-Path (Split-Path -Parent $PSScriptRoot) 'NuGet.Config')" -- $output
if ($LASTEXITCODE -ne 0) { throw "WinSkitch tests failed (exit code $LASTEXITCODE)." }
