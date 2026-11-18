$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = 'yuchen0x1/XiaFlow'
$release = gh api "repos/$repo/releases/latest" | ConvertFrom-Json
if ($release.draft -or $release.prerelease -or !$release.immutable) { throw 'Expected an immutable stable release.' }
$tag = $release.tag_name
if ($tag -cnotmatch '^v\d+\.\d+\.\d+$') { throw 'Unexpected release tag.' }
$version = $tag.Substring(1)
$installer = "XiaFlow-$version-setup-x64.exe"
$names = @($installer, "XiaFlow-$version-portable-x64.zip", "XiaFlow-$version-cli.zip", '7z2606-src.tar.xz',
    'SHA256SUMS.txt', 'update-windows-x64.json', 'update-windows-x64.sig')
foreach ($name in $names) {
    $asset = $release.assets | Where-Object name -CEQ $name
    if (!$asset -or $asset.state -ne 'uploaded' -or $asset.digest -cnotmatch '^sha256:[0-9a-f]{64}$') {
        throw "Unexpected release asset state: $name"
    }
    if ($asset.browser_download_url -cne "https://github.com/$repo/releases/download/$tag/$name") { throw "Unexpected URL: $name" }
    curl --fail --silent --show-error --location --head --max-time 30 $asset.browser_download_url | Out-Null
}
gh release verify $tag --repo $repo
$dir = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ("xiaflow-release-check-" + [Guid]::NewGuid().ToString('n')))).FullName
gh release download $tag --repo $repo --pattern 'update-windows-x64.*' --dir $dir
$manifest = Join-Path $dir 'update-windows-x64.json'
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $public = (Get-Content (Join-Path $PSScriptRoot '../src/XiaFlow.Core/Update/public-key.txt') -Raw).Trim()
    $rsa.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($public), [ref]$null)
    $valid = $rsa.VerifyData([IO.File]::ReadAllBytes($manifest), [IO.File]::ReadAllBytes("$dir/update-windows-x64.sig"),
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)
    $data = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $asset = $release.assets | Where-Object name -CEQ $installer
    if (!$valid -or $data.version -cne $version -or $data.asset -cne $installer -or $data.bytes -ne $asset.size -or
        "sha256:$($data.sha256)" -cne $asset.digest) { throw 'Update manifest does not describe the attested installer.' }
} finally {
    $rsa.Dispose()
}
Write-Host "PASS: $tag asset links, immutable release attestation and signed updater metadata."
