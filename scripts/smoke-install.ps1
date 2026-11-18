param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$registration = 'Software/Microsoft/Windows/CurrentVersion/Uninstall/{8E4F5A1C-6B2D-4E9A-9C71-8A7F10F10000}_is1'
if ((Test-Path "HKCU:/$registration") -or (Test-Path "HKLM:/$registration")) {
    throw 'Use a clean Windows test account. XiaFlow is already installed.'
}

$setup = (Resolve-Path "artifacts/release/XiaFlow-$Version-setup-x64.exe").Path
$root = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ("XiaFlow-smoke-" + [guid]::NewGuid().ToString('n')))).FullName
$oldSetup = Join-Path $root 'XiaFlow-1.0.0-setup-x64.exe'
$install = Join-Path $root 'program'
$env:XIAFLOW_DATA_DIR = Join-Path $root 'data'
$exe = Join-Path $install 'XiaFlow.exe'
$sentinel = Join-Path $env:XIAFLOW_DATA_DIR 'backup/preserve.txt'

function Install([string]$Binary, [string]$UpdateFlag) {
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /TASKS=`"`" /DIR=`"$install`" $UpdateFlag"
    $proc = Start-Process -FilePath $Binary -ArgumentList $arguments -PassThru
    if (!$proc.WaitForExit(180000) -or $proc.ExitCode -ne 0) { throw "Setup failed: $Binary." }
}

try {
    Invoke-WebRequest -Uri 'https://github.com/yuchen0x1/XiaFlow/releases/download/v1.0.0/XiaFlow-1.0.0-setup-x64.exe' -OutFile $oldSetup
    if ((Get-FileHash -LiteralPath $oldSetup).Hash -ne '4574992D985A8587DC0537E962926CC41C979A759A77F063A0B6F99456C269AD') {
        throw 'Baseline installer checksum mismatch.'
    }
    Install $oldSetup ''
    if ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -notmatch '^1\.0\.0(?:\+|$)') { throw 'Baseline binary was not installed.' }
    [void](New-Item -ItemType Directory (Split-Path -Parent $sentinel) -Force)
    'preserve backup state' | Set-Content -LiteralPath $sentinel

    Install $setup '/XIAFLOWUPDATE'
    $installed = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    if ($installed -notmatch "^$([regex]::Escape($Version))(?:\+|$)") { throw "Installed wrong version: $installed." }
    if (!(Test-Path -LiteralPath (Join-Path $install 'xiaflow-installed.ini'))) { throw 'Installation marker missing.' }
    if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'preserve backup state') { throw 'Backup data changed during upgrade.' }

    $app = $null
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $app = Get-Process XiaFlow -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe -and $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
        if ($app) { break }
        Start-Sleep -Seconds 1
    }
    if (!$app) { throw 'The updated app did not reopen its main window.' }
    Stop-Process -Id $app.Id

    $uninstaller = Get-ChildItem -LiteralPath $install -Filter 'unins*.exe' -File | Select-Object -First 1
    $process = Start-Process -FilePath $uninstaller.FullName -ArgumentList '/VERYSILENT /NORESTART' -PassThru -Wait
    if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath $exe)) { throw 'Uninstall failed.' }
    if (!(Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed backup data.' }
    Write-Host 'PASS: installed 1.0.0, upgraded, reopened, uninstalled and retained backup data.'
} finally {
    Get-Process XiaFlow -ErrorAction SilentlyContinue | Where-Object Path -eq $exe | Stop-Process
}
