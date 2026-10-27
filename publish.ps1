param([string]$DotnetPath)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $DotnetPath) {
    $localDotnet = Join-Path $env:LOCALAPPDATA 'TSCBr\BuildTools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet) { $DotnetPath = $localDotnet }
    else {
        $command = Get-Command dotnet -ErrorAction Stop
        $DotnetPath = $command.Source
    }
}

$project = Join-Path $PSScriptRoot 'src\TSCBr.OfficeInstaller\TSCBr.OfficeInstaller.csproj'
$destination = Join-Path $PSScriptRoot 'dist\win-x64'
& $DotnetPath publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    --output $destination --nologo
if ($LASTEXITCODE -ne 0) { throw 'A publicação falhou.' }

$executable = Join-Path $destination 'TSCBr_InstaladorOffice.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'O executável não foi gerado.' }
Write-Host ('Executável pronto: ' + $executable)
Get-FileHash -LiteralPath $executable -Algorithm SHA256
