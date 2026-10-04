param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $workspace '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $workspace '.tools\nuget'
$project = Join-Path $PSScriptRoot 'WinSkitch\WinSkitch.csproj'
$localDotnet = Join-Path $workspace '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
} else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) {
        throw '.NET 10 SDK is required. Install the SDK or place it in .tools\dotnet, then run build.ps1 again. Published WinSkitch.exe does not require the SDK.'
    }
    $dotnet = $command.Source
}

& $dotnet build $project --configuration $Configuration --nologo -p:TargetPlatformDisplayName=Windows `
    "-p:RestoreConfigFile=$(Join-Path $PSScriptRoot 'NuGet.Config')"
if ($LASTEXITCODE -ne 0) { throw "WinSkitch build failed (exit code $LASTEXITCODE). Check that .NET 10 SDK is installed." }
Write-Host "Built: $PSScriptRoot\WinSkitch\bin\$Configuration\net10.0-windows\WinSkitch.exe"
