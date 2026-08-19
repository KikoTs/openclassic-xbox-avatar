[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path
$gameExe = Join-Path $gameRoot 'CastleMinerZ.exe'
$commonDll = Join-Path $gameRoot 'DNA.Common.dll'

foreach ($required in @($gameExe, $commonDll)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Missing required user-supplied reference: $required"
    }
}

# 1.9.9 moved the stock proxy model entity out of DNA.Common and renamed it
# DNA.CastleMinerZ.PlayerModelEntity. The runtime aliases whichever name the
# target client actually defines, so detect it here. Type names live as UTF-8
# in the metadata string heap, which makes a byte scan sufficient and keeps
# this script free of a metadata-reader dependency.
function Test-ModernModelEntity {
    param([Parameter(Mandatory = $true)][string]$Assembly)

    $needle = [Text.Encoding]::UTF8.GetBytes('PlayerModelEntity')
    $bytes = [IO.File]::ReadAllBytes($Assembly)
    $last = $bytes.Length - $needle.Length
    for ($i = 0; $i -le $last; $i++) {
        if ($bytes[$i] -ne $needle[0]) { continue }
        $match = $true
        for ($j = 1; $j -lt $needle.Length; $j++) {
            if ($bytes[$i + $j] -ne $needle[$j]) { $match = $false; break }
        }
        if ($match) { return $true }
    }
    return $false
}

$modernModelEntity = Test-ModernModelEntity -Assembly $gameExe
if ($modernModelEntity) {
    Write-Host 'Target client defines PlayerModelEntity (1.9.9 or later).' -ForegroundColor Cyan
} else {
    Write-Host 'Target client defines AvatarModelEntity (pre-1.9.9).' -ForegroundColor Cyan
}

$artifacts = Join-Path $repoRoot 'artifacts'
$bin = Join-Path $artifacts 'bin'
$obj = Join-Path $artifacts 'obj'
if (Test-Path -LiteralPath $artifacts) {
    $resolvedArtifacts = (Resolve-Path -LiteralPath $artifacts).Path
    if (-not $resolvedArtifacts.StartsWith(
        $repoRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an artifacts path outside the repository: $resolvedArtifacts"
    }
    Remove-Item -LiteralPath $resolvedArtifacts -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $bin, $obj | Out-Null

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Find-XnaAssembly {
    param([Parameter(Mandatory = $true)][string]$Name)

    $assembly = Get-ChildItem -Path (
        Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_32\$Name\v4.0_*\$Name.dll"
    ) -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $assembly) {
        throw "Could not find $Name in the XNA Framework 4.0 GAC."
    }
    return $assembly.FullName
}

$frameworkCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $frameworkCsc)) {
    throw 'The .NET Framework 4 compiler was not found.'
}

$xnaFramework = Find-XnaAssembly 'Microsoft.Xna.Framework'
$xnaGraphics = Find-XnaAssembly 'Microsoft.Xna.Framework.Graphics'
$xnaGame = Find-XnaAssembly 'Microsoft.Xna.Framework.Game'

$runtimeOut = Join-Path $bin 'OpenClassicAvatarMod.dll'
$runtimeSource = Join-Path $repoRoot 'src\Runtime\OpenClassicAvatarMod.cs'
$runtimeArguments = @(
    '/nologo', '/target:library', '/optimize+', '/platform:x86',
    "/out:$runtimeOut",
    "/reference:$commonDll",
    "/reference:$gameExe",
    "/reference:$xnaFramework",
    "/reference:$xnaGraphics",
    "/reference:$xnaGame",
    $runtimeSource
)
if ($modernModelEntity) {
    $runtimeArguments = @('/define:CMZ_MODERN_MODEL_ENTITY') + $runtimeArguments
}
Invoke-Native 'Runtime compilation' { & $frameworkCsc $runtimeArguments }

$importerOut = Join-Path $bin 'Import Xbox Avatar.exe'
$importerSource = Join-Path $repoRoot 'src\Importer\OpenClassicAvatarImporter.cs'
$importerArguments = @(
    '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu',
    "/out:$importerOut",
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll',
    $importerSource
)
Invoke-Native 'Importer compilation' { & $frameworkCsc $importerArguments }

foreach ($testName in @('AvatarProtocolSmoke', 'AvatarMessageIdSmoke')) {
    $testSource = Join-Path $repoRoot "tests\Protocol\$testName.cs"
    $testOut = Join-Path $bin "$testName.exe"
    $testArguments = @(
        '/nologo', '/target:exe', '/optimize+', '/platform:x86',
        "/out:$testOut",
        $testSource
    )
    Invoke-Native "$testName compilation" { & $frameworkCsc $testArguments }
}

$attachmentTestSource = Join-Path $repoRoot 'tests\Attachment\AvatarAttachmentSmoke.cs'
$attachmentTestOut = Join-Path $bin 'AvatarAttachmentSmoke.exe'
$attachmentTestArguments = @(
    '/nologo', '/target:exe', '/optimize+', '/platform:x86',
    "/out:$attachmentTestOut",
    "/reference:$commonDll",
    "/reference:$xnaFramework",
    $attachmentTestSource
)
Invoke-Native 'AvatarAttachmentSmoke compilation' {
    & $frameworkCsc $attachmentTestArguments
}

$managerProject = Join-Path $repoRoot 'src\Manager\AvatarModPatcher.csproj'
$managerPublish = Join-Path $obj 'manager-publish'
Invoke-Native 'Manager publication' {
    & dotnet publish $managerProject `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $managerPublish
}
Copy-Item -LiteralPath (Join-Path $managerPublish 'AvatarModPatcher.exe') `
    -Destination (Join-Path $bin 'OpenClassic Xbox Avatar Manager.exe')

$avatarPackage = Get-AppxPackage -Name 'Microsoft.Avatars' |
    Sort-Object Version -Descending |
    Select-Object -First 1
if (-not $avatarPackage -or -not $avatarPackage.InstallLocation) {
    throw 'Xbox Original Avatars is not installed for the current user.'
}

$cppWinRt = Get-ChildItem -Path (
    Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\*\x64\cppwinrt.exe'
) -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
    Select-Object -First 1
if (-not $cppWinRt) {
    throw 'cppwinrt.exe was not found in the Windows SDK.'
}

$generated = Join-Path $obj 'generated'
New-Item -ItemType Directory -Force -Path $generated | Out-Null
Invoke-Native 'C++/WinRT projection generation' {
    & $cppWinRt.FullName `
        -input $avatarPackage.InstallLocation `
        -reference sdk `
        -output $generated
}

$vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vsWhere)) {
    throw 'Visual Studio Installer vswhere.exe was not found.'
}
$visualStudio = & $vsWhere `
    -latest `
    -products '*' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $visualStudio) {
    throw 'Visual Studio C++ x64 build tools were not found.'
}
$devShell = Join-Path ($visualStudio | Select-Object -Last 1) `
    'Common7\Tools\Launch-VsDevShell.ps1'
if (-not (Test-Path -LiteralPath $devShell)) {
    throw "Visual Studio developer shell was not found: $devShell"
}
& $devShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation

$nativeObj = Join-Path $obj 'native'
New-Item -ItemType Directory -Force -Path $nativeObj | Out-Null
$bridgeOut = Join-Path $bin 'AvatarBridge.dll'
$injectorOut = Join-Path $bin 'AvatarBridgeInjector.exe'
$bridgeSource = Join-Path $repoRoot 'src\Bridge\AvatarBridge.cpp'
$injectorSource = Join-Path $repoRoot 'src\Bridge\AvatarBridgeInjector.cpp'

Push-Location $nativeObj
try {
    Invoke-Native 'Avatar bridge compilation' {
        & cl.exe `
            /nologo /std:c++20 /EHsc /O2 /MD /LD /DUNICODE /D_UNICODE `
            "/I$generated" `
            $bridgeSource `
            /link "/OUT:$bridgeOut" runtimeobject.lib windowsapp.lib crypt32.lib
    }
    Invoke-Native 'Avatar bridge injector compilation' {
        & cl.exe `
            /nologo /std:c++20 /EHsc /O2 /MD /DUNICODE /D_UNICODE `
            $injectorSource `
            /link /SUBSYSTEM:CONSOLE "/OUT:$injectorOut"
    }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'Build completed:' -ForegroundColor Green
Get-ChildItem -LiteralPath $bin -File |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        Write-Host ("  {0}  {1}" -f $hash, $_.Name)
    }
