$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

dotnet restore XiaFlow.slnx -p:NuGetAudit=true -p:NuGetAuditMode=all '-p:WarningsAsErrors=NU1901%3BNU1902%3BNU1903%3BNU1904'
if ($LASTEXITCODE -ne 0) { throw 'Restore or NuGet vulnerability audit failed.' }
dotnet build XiaFlow.slnx -c Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
dotnet test XiaFlow.slnx -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
