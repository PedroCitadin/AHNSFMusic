# Copies externally verified spotDL/FFmpeg into a NEW publish folder and fills Tools/manifest.json.
# Never downloads anything. Hashes must come from the publisher (e.g. the GitHub release digest), not from the file itself.
# FFmpeg can be given as ffmpeg.exe + its hash, or as the publisher's archive + the archive hash: the executable hash is then
# derived only from that verified archive.
[CmdletBinding(DefaultParameterSetName = 'Executable')]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$SpotDlExecutable,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$SpotDlSha256,
    [Parameter(Mandatory)][string]$SpotDlLicense,
    [Parameter(Mandatory, ParameterSetName = 'Executable')][string]$FFmpegExecutable,
    [Parameter(Mandatory, ParameterSetName = 'Executable')][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$FFmpegSha256,
    [Parameter(Mandatory, ParameterSetName = 'Executable')][string]$FFmpegLicense,
    [Parameter(Mandatory, ParameterSetName = 'Archive')][string]$FFmpegArchive,
    [Parameter(Mandatory, ParameterSetName = 'Archive')][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$FFmpegArchiveSha256
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PublishDirectory).Path
$manifestPath = Join-Path $package 'Tools/manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Publicação sem Tools/manifest.json; publique o aplicativo antes.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.SpotDlVersion -ne '4.5.2' -or $manifest.FFmpegVersion -ne '9.0.2') { throw 'Versões do manifesto incompatíveis.' }
$extracted = $null
try {
    if ($PSCmdlet.ParameterSetName -eq 'Archive') {
        $archive = (Resolve-Path -LiteralPath $FFmpegArchive).Path
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $FFmpegArchiveSha256.ToUpperInvariant()) { throw 'SHA-256 do pacote FFmpeg diferente do publicado.' }
        $extracted = Join-Path ([IO.Path]::GetTempPath()) ('ahnsf-ffmpeg-' + [guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $archive -DestinationPath $extracted
        $FFmpegExecutable = @(Get-ChildItem -LiteralPath $extracted -Recurse -Filter 'ffmpeg.exe' | Where-Object { $_.Directory.Name -eq 'bin' })[0].FullName
        $FFmpegLicense = @(Get-ChildItem -LiteralPath $extracted -Recurse -Filter 'LICENSE*' -File)[0].FullName
        if (-not $FFmpegExecutable -or -not $FFmpegLicense) { throw 'Pacote FFmpeg sem bin/ffmpeg.exe ou LICENSE.' }
        $FFmpegSha256 = (Get-FileHash -LiteralPath $FFmpegExecutable -Algorithm SHA256).Hash
    }
    $sources = @(
        @{ Source = $SpotDlExecutable; Hash = $SpotDlSha256.ToUpperInvariant(); License = $SpotDlLicense; Subdirectory = 'spotdl'; Name = 'spotdl.exe'; VersionArgument = '--version'; Version = '4.5.2' },
        @{ Source = $FFmpegExecutable; Hash = $FFmpegSha256.ToUpperInvariant(); License = $FFmpegLicense; Subdirectory = 'ffmpeg'; Name = 'ffmpeg.exe'; VersionArgument = '-version'; Version = '9.0.2' }
    )
    foreach ($tool in $sources) {
        $tool.Source = (Resolve-Path -LiteralPath $tool.Source).Path
        $tool.License = (Resolve-Path -LiteralPath $tool.License).Path
        if ((Get-FileHash -LiteralPath $tool.Source -Algorithm SHA256).Hash -ne $tool.Hash) { throw "SHA-256 de $($tool.Name) diferente do esperado." }
        # spotDL creates ~/.spotdl on start; run the check with a throwaway profile so the operator's profile is untouched.
        $probeHome = Join-Path ([IO.Path]::GetTempPath()) ('ahnsf-probe-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $probeHome | Out-Null
        $savedProfile = $env:USERPROFILE
        try { $env:USERPROFILE = $probeHome; $versionText = (& $tool.Source $tool.VersionArgument 2>&1 | Out-String) }
        finally { $env:USERPROFILE = $savedProfile; Remove-Item -LiteralPath $probeHome -Recurse -Force -ErrorAction SilentlyContinue }
        if ($LASTEXITCODE -ne 0 -or -not $versionText.Contains($tool.Version)) { throw "Versão de $($tool.Name) incompatível." }
        $destination = Join-Path $package "Tools/$($tool.Subdirectory)"
        if (Test-Path -LiteralPath $destination) { throw "Destino $destination já existe; use uma publicação nova." }
    }
    foreach ($tool in $sources) {
        $destination = Join-Path $package "Tools/$($tool.Subdirectory)"
        New-Item -ItemType Directory -Path $destination | Out-Null
        Copy-Item -LiteralPath $tool.Source -Destination (Join-Path $destination $tool.Name)
        Copy-Item -LiteralPath $tool.License -Destination (Join-Path $destination 'LICENSE.txt')
        if ((Get-FileHash -LiteralPath (Join-Path $destination $tool.Name) -Algorithm SHA256).Hash -ne $tool.Hash) { throw "Cópia de $($tool.Name) falhou na verificação." }
    }
    $manifest.SpotDlSha256 = $sources[0].Hash
    $manifest.FFmpegSha256 = $sources[1].Hash
    $manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Host "Ferramentas verificadas em $package. Revise obrigações de licença e valide o pacote em uma máquina limpa."
}
finally {
    if ($extracted -and (Test-Path -LiteralPath $extracted)) { Remove-Item -LiteralPath $extracted -Recurse -Force }
}
