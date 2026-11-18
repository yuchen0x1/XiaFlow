param([Parameter(Mandatory)][string]$Version, [string]$AssetDir = 'artifacts/release')
$ErrorActionPreference = 'Stop'
if ($Version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Expected a stable product version.' }
if (-not $env:XIAFLOW_UPDATE_SIGNING_KEY) { throw 'The release signing key is required.' }

$keyPath = Join-Path $PSScriptRoot '../src/XiaFlow.Core/Update/public-key.txt'
$publicKey = [IO.File]::ReadAllText($keyPath).Trim()
$filename = "XiaFlow-$Version-setup-x64.exe"
$installer = Get-Item -LiteralPath (Join-Path $AssetDir $filename) -ErrorAction Stop
$manifest = [ordered]@{
    schema = 1
    version = $Version
    platform = 'windows'
    architecture = 'x64'
    minimumWindowsBuild = 17763
    asset = $filename
    bytes = $installer.Length
    sha256 = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$rsa = [Security.Cryptography.RSA]::Create()
$key = [Convert]::FromBase64String($env:XIAFLOW_UPDATE_SIGNING_KEY)
try {
    $rsa.ImportPkcs8PrivateKey($key, [ref]$null)
    if ([Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo()) -cne $publicKey) { throw 'Signing key does not match the pinned update key.' }
    $data = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json))
    $signature = $rsa.SignData($data, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)
    [IO.File]::WriteAllBytes((Join-Path $AssetDir 'update-windows-x64.json'), $data)
    [IO.File]::WriteAllBytes((Join-Path $AssetDir 'update-windows-x64.sig'), $signature)
} finally {
    [Array]::Clear($key)
    $rsa.Dispose()
}
