param([string]$Iscc = '', [switch]$Installer)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content Directory.Build.props -Raw)).SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Directory.Build.props must declare a stable version.' }

$portable = 'artifacts/portable/XiaFlow'
if (Test-Path -LiteralPath $portable) { throw 'Use a clean artifacts/portable/XiaFlow output directory.' }
dotnet publish src/XiaFlow.App -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -o $portable --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$required = @('engine/x64/7z.exe','engine/x64/7z.dll','licenses/7-Zip-License.txt','XiaFlow.exe')
foreach ($file in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $portable $file) -PathType Leaf)) { throw "Missing required file: $file" }
}
Copy-Item LICENSE,THIRD-PARTY-NOTICES.md "$portable/licenses/"
if (Get-ChildItem $portable -Recurse -Filter '*.pdb') { throw 'Public packages must exclude PDB files.' }
if (!$Installer) { return }

if (!$Iscc) {
    $Iscc = @(
        "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe",
        "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (!$Iscc) { throw 'Install Inno Setup 6 or pass -Iscc.' }

& $Iscc "/DMyAppVersion=$version" 'installer/XiaFlow.iss'
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed.' }

$release = 'artifacts/release'
if (Test-Path -LiteralPath $release) { throw 'Use a clean artifacts/release output directory.' }
[void](New-Item -ItemType Directory $release)
Copy-Item "artifacts/installer/XiaFlow-$version-setup-x64.exe" $release
Compress-Archive -Path "$portable/*" -DestinationPath "$release/XiaFlow-$version-portable-x64.zip"

$cli = 'artifacts/cli'
if (Test-Path -LiteralPath $cli) { throw 'Use a clean artifacts/cli output directory.' }
dotnet publish src/XiaFlow.Cli -c Release --no-self-contained -p:UseAppHost=false -p:DebugType=none -p:DebugSymbols=false -o $cli --nologo
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
Copy-Item LICENSE,THIRD-PARTY-NOTICES.md $cli
Compress-Archive -Path "$cli/*" -DestinationPath "$release/XiaFlow-$version-cli.zip"

$source = Join-Path $release '7z2606-src.tar.xz'
Invoke-WebRequest -Uri 'https://github.com/ip7z/7zip/releases/download/26.06/7z2606-src.tar.xz' -OutFile $source
if ((Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -cne 'a4e374c8f864f51df48c2cfd0e1413ec3b2ea030deae852d6a655402ebb61ac4') {
    throw '7-Zip source checksum mismatch.'
}
