param([string]$DotnetPath)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $DotnetPath) {
    $localDotnet = Join-Path $env:LOCALAPPDATA 'TSCBr\BuildTools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet) { $DotnetPath = $localDotnet }
    else { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$project = Join-Path $PSScriptRoot 'tools\TSCBr.OfficeInstaller.Verification\TSCBr.OfficeInstaller.Verification.csproj'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
& $DotnetPath run --project $project -c Release -- $artifacts
if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
