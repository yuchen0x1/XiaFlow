param([string]$Cli = 'src/XiaFlow.Cli/bin/Release/net10.0/xiaflow-cli.dll')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$cliPath = (Resolve-Path -LiteralPath $Cli).Path
$root = (New-Item -Path (Join-Path ([IO.Path]::GetTempPath()) ("xiaflow-cli-" + [Guid]::NewGuid().ToString('n'))) -ItemType Directory).FullName
$env:XIAFLOW_DATA_DIR = Join-Path $root 'data'
if ($IsWindows) {
    $env:XIAFLOW_ENGINE_DIR = (Resolve-Path 'resources/engine/x64').Path
} else {
    $target = if ($IsLinux) { 'linux-x64' } elseif ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'mac' } else { 'mac-x64' }
    $hash = @{
        'linux-x64' = '2b539cf067b2ad48e1baa5093dbaa8818b73542d1222a0ffde9dc5f2922ea62e'
        'mac' = 'b6f17920b4240940461dac8ef4009891881ac44d2a5ead625ede2ec8472185dd'
        'mac-x64' = '3af68b124052da7fc2a246c96b0bea6a68bfee389baa788f4cead1a133445586'
    }[$target]
    $archive = Join-Path $root 'engine.tar.xz'
    Invoke-WebRequest -Uri "https://github.com/ip7z/7zip/releases/download/26.06/7z2606-$target.tar.xz" -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -cne $hash) { throw '7-Zip engine digest did not match the pinned version.' }
    tar -xJf $archive -C $root
    $env:XIAFLOW_ENGINE_DIR = $root
}
Push-Location $root
try {
    $source = '资料 入门.txt'
    'XiaFlow CLI UTF-8 fixture' | Set-Content -LiteralPath $source
    $expected = (Get-FileHash -LiteralPath $source).Hash
    foreach ($extension in @('zip','7z')) {
        $archive = "sample.$extension"
        $out = "extracted-$extension"
        dotnet $cliPath create $archive $source
        $entries = dotnet $cliPath list $archive --json | ConvertFrom-Json
        if ($entries.Count -ne 1 -or $entries[0].path -cne $source) { throw 'Incorrect list output.' }
        dotnet $cliPath test $archive
        dotnet $cliPath extract $archive $out
        if ((dotnet $cliPath sha256 (Join-Path $out $source)) -ne $expected) { throw 'Extracted contents differ.' }
        $PSNativeCommandUseErrorActionPreference = $false
        dotnet $cliPath extract $archive $out
        if ($LASTEXITCODE -ne 1) { throw 'CLI failed to protect an existing destination.' }
        $PSNativeCommandUseErrorActionPreference = $true
    }
    dotnet $cliPath --version
} finally {
    Pop-Location
}
