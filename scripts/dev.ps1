param([ValidateSet('Restore','Build','Test','Validate','Run')][string]$Action = 'Run',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$localSdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$sdkCommand = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
function Invoke-DotNet([string[]]$Arguments) {
    & $sdkCommand @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet terminou com código $LASTEXITCODE." }
}
switch ($Action) {
    'Restore' { Invoke-DotNet @('restore','AHNSF.Music.sln','--disable-parallel','-m:1') }
    'Build' { Invoke-DotNet @('build','AHNSF.Music.sln','-c',$Configuration,'--no-restore','-m:1','-p:UseSharedCompilation=false') }
    'Test' { Invoke-DotNet @('test','AHNSF.Music.sln','-c',$Configuration,'--no-build','--no-restore','-m:1','--logger','trx;LogFileName=phase081.trx') }
    'Validate' { & $PSCommandPath Restore; & $PSCommandPath Build -Configuration Debug; & $PSCommandPath Build -Configuration Release; & $PSCommandPath Test -Configuration Release }
    'Run' { Invoke-DotNet @('run','--project','src/AHNSF.Music.Desktop','-c',$Configuration,'--no-restore') }
}


