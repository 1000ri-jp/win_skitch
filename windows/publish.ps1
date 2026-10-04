param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$env:DOTNET_CLI_HOME = Join-Path $workspace '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $workspace '.tools\nuget'
$project = Join-Path $PSScriptRoot 'WinSkitch\WinSkitch.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $output = Join-Path $workspace "artifacts\$Runtime"
} elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $output = [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    $output = [System.IO.Path]::GetFullPath((Join-Path $workspace $OutputDirectory))
}
$workspacePrefix = $workspace.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($workspacePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be a directory inside the repository workspace.'
}
$localDotnet = Join-Path $workspace '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
} else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) {
        throw '.NET 10 SDK is required to publish. Install the SDK or place it in .tools\dotnet, then run publish.ps1 again.'
    }
    $dotnet = $command.Source
}

& $dotnet publish $project --configuration $Configuration --runtime $Runtime --self-contained true --output $output --nologo `
    -p:TargetPlatformDisplayName=Windows "-p:RestoreConfigFile=$(Join-Path $PSScriptRoot 'NuGet.Config')" `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) { throw "WinSkitch publish failed (exit code $LASTEXITCODE)." }
Write-Host "Ready to share: $output\WinSkitch.exe"
