[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $repoRoot 'build.ps1') -GameDirectory $GameDirectory

$artifacts = Join-Path $repoRoot 'artifacts'
$bin = Join-Path $artifacts 'bin'
$packageRoot = Join-Path $artifacts 'package'
$stage = Join-Path $packageRoot 'OpenClassic-XboxAvatar-Addon'
$zipPath = Join-Path $artifacts 'OpenClassic-XboxAvatar-Addon.zip'

if (Test-Path -LiteralPath $packageRoot) {
    $resolvedPackageRoot = (Resolve-Path -LiteralPath $packageRoot).Path
    if (-not $resolvedPackageRoot.StartsWith(
        $artifacts + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a package path outside artifacts: $resolvedPackageRoot"
    }
    Remove-Item -LiteralPath $resolvedPackageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path `
    $stage, `
    (Join-Path $stage 'OpenClassic Addons\Xbox Avatar'), `
    (Join-Path $stage 'OpenClassic Addons\Xbox Avatar Bridge') | Out-Null

foreach ($name in @(
    'OpenClassicAvatarMod.dll',
    'Import Xbox Avatar.exe',
    'OpenClassic Xbox Avatar Manager.exe'
)) {
    Copy-Item -LiteralPath (Join-Path $bin $name) -Destination (Join-Path $stage $name)
}
Copy-Item -LiteralPath (Join-Path $bin 'AvatarBridge.dll') `
    -Destination (Join-Path $stage 'OpenClassic Addons\Xbox Avatar Bridge\AvatarBridge.dll')
Copy-Item -LiteralPath (Join-Path $bin 'AvatarBridgeInjector.exe') `
    -Destination (Join-Path $stage 'OpenClassic Addons\Xbox Avatar Bridge\AvatarBridgeInjector.exe')
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\Disable Xbox Avatar.cmd') `
    -Destination (Join-Path $stage 'Disable Xbox Avatar.cmd')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\INSTALL.md') `
    -Destination (Join-Path $stage 'INSTALL.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\ARCHITECTURE.md') `
    -Destination (Join-Path $stage 'TECHNICAL.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\INSTALL.md') `
    -Destination (Join-Path $stage 'OpenClassic Addons\Xbox Avatar\README.md')

$forbidden = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object {
    $_.Name -ieq 'CastleMinerZ.exe' -or
    $_.Name -ieq 'DNA.Common.dll' -or
    $_.Extension -ieq '.cs' -or
    $_.Extension -ieq '.winmd' -or
    $_.Extension -ieq '.ocavatar'
}
if ($forbidden) {
    throw "Package contains forbidden files: $($forbidden.FullName -join ', ')"
}

$checksumLines = Get-ChildItem -LiteralPath $stage -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($stage.Length + 1)
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        "$hash  $relative"
    }
Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') `
    -Value $checksumLines `
    -Encoding utf8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath $stage -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ''
Write-Host 'Source-only package audit passed.' -ForegroundColor Green
Write-Host "Package: $zipPath"
Write-Host "SHA-256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash)"
