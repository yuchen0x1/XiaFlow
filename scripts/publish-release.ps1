param([Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($Tag -cnotmatch '^v(?<version>\d+\.\d+\.\d+)$') { throw 'Expected a stable release tag.' }
$version = $Matches.version
$dir = 'artifacts/release'
$assetNames = @("XiaFlow-$version-setup-x64.exe", "XiaFlow-$version-portable-x64.zip", "XiaFlow-$version-cli.zip",
    '7z2606-src.tar.xz', 'update-windows-x64.json', 'update-windows-x64.sig')
$files = $assetNames | ForEach-Object { Get-Item -LiteralPath (Join-Path $dir $_) -ErrorAction Stop }
$hashes = @{}
$checksum = Join-Path $dir 'SHA256SUMS.txt'
$files | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashes[$_.Name] = "sha256:$hash"
    "$hash  $($_.Name)"
} | Set-Content -LiteralPath $checksum
$hashes['SHA256SUMS.txt'] = "sha256:$((Get-FileHash -LiteralPath $checksum -Algorithm SHA256).Hash.ToLowerInvariant())"

# Create refuses to replace an existing draft or published release.
gh release create $Tag --verify-tag --draft --title "匣流 XiaFlow $Tag" --notes-file artifacts/release-notes.md
gh release upload $Tag @($files.FullName) $checksum
$release = gh release view $Tag --json assets,isDraft | ConvertFrom-Json
if (!$release.isDraft -or $release.assets.Count -ne $hashes.Count) { throw 'Unexpected release or asset set.' }
foreach ($asset in $release.assets) {
    if ($asset.state -ne 'uploaded' -or $hashes[$asset.name] -cne $asset.digest) { throw "Asset digest mismatch: $($asset.name)" }
}
gh release edit $Tag --draft=false --latest
gh release verify $Tag
