param([switch]$Tray)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$publishedPaths = @(
    (Join-Path $workspace 'artifacts\win-x64\WinSkitch.exe'),
    (Join-Path $workspace 'artifacts\win-x64-updated\WinSkitch.exe')
)
$published = $publishedPaths | Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { Get-Item -LiteralPath $_ } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($published) {
    $executable = $published.FullName
} else {
    & (Join-Path $PSScriptRoot 'build.ps1')
    $executable = Join-Path $PSScriptRoot 'WinSkitch\bin\Release\net10.0-windows\WinSkitch.exe'
    $localRuntime = Join-Path $workspace '.tools\dotnet'
    if (Test-Path -LiteralPath $localRuntime) { $env:DOTNET_ROOT = $localRuntime }
}
if ($Tray) {
    Start-Process -FilePath $executable -ArgumentList '--tray' -WindowStyle Hidden
} else {
    Start-Process -FilePath $executable
}
