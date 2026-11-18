# 构建便携版 XiaFlow（单目录，含引擎与许可证）
param([string]$OutDir = "artifacts")
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

dotnet publish src/XiaFlow.App/XiaFlow.App.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$OutDir/portable/XiaFlow" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish 失败" }

# 校验引擎与许可证随包分发
$root = Join-Path (Get-Location) "$OutDir/portable/XiaFlow"
@("engine\x64\7z.exe", "engine\x64\7z.dll", "licenses\7-Zip-License.txt", "XiaFlow.exe") | ForEach-Object {
    if (!(Test-Path (Join-Path $root $_))) { throw "缺少必需文件: $_" }
}
$size = [math]::Round((Get-ChildItem $root -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "PORTABLE_OK $root ($size MB)"
