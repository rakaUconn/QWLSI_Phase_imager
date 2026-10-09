<#
  Clean-location test of the release artifacts (no VM available -> minimal environment instead):
    -Mode Portable : unzip the portable zip into a fresh folder and run the app there
    -Mode Installer: silent per-user install into a fresh folder, run, check shortcuts + uninstall entry, uninstall, check clean removal
  The app is started through its scripted smoke test (--selftest): full offline reconstruction, with-reference run, Zernike filter + exports,
  3-D view (hardware and WARP, with mouse input), About box, crash reporter. The environment is stripped: PATH = Windows only (no dotnet, no
  python, no MKL on PATH), no DOTNET_*, no developer variables.
#>
param(
    [ValidateSet('Portable', 'Installer')][string]$Mode = 'Portable',
    [string]$Release = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\release'),
    [string]$Work = 'C:\QwlsiCleanTest',
    [string]$Version = '1.1.0',
    [string]$MklIsa = ''          # e.g. AVX2 / SSE4_2 to force another MKL kernel set (checks the trimmed MKL files)
)
$ErrorActionPreference = 'Stop'
$ex = Join-Path $Work 'examples'
function Run-Clean([string]$exe, [string[]]$arguments, [int]$timeoutSec = 400) {
    $psi = New-Object Diagnostics.ProcessStartInfo $exe
    $psi.UseShellExecute = $false; $psi.WorkingDirectory = $Work
    $psi.Arguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $psi.Environment.Clear()
    foreach ($k in 'SystemRoot', 'windir', 'SystemDrive', 'TEMP', 'TMP', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'ProgramData', 'ProgramFiles', 'COMPUTERNAME', 'USERNAME', 'USERDOMAIN', 'NUMBER_OF_PROCESSORS', 'PROCESSOR_ARCHITECTURE', 'OS') { $v = [Environment]::GetEnvironmentVariable($k); if ($v) { $psi.Environment[$k] = $v } }
    $psi.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"      # minimal PATH: nothing from dotnet / python / Intel / dev tools
    if ($MklIsa) { $psi.Environment['MKL_ENABLE_INSTRUCTIONS'] = $MklIsa }
    $p = [Diagnostics.Process]::Start($psi); $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $timeoutSec) { Start-Sleep -Milliseconds 500 }
    if (-not $p.HasExited) { $p.Kill(); return @{ Exited = $false; Code = -1; Seconds = $sw.Elapsed.TotalSeconds } }
    return @{ Exited = $true; Code = $p.ExitCode; Seconds = $sw.Elapsed.TotalSeconds }
}

New-Item -ItemType Directory -Force $Work | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not (Test-Path $ex)) { Write-Host '== extracting examples'; [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $Release "QwlsiApp-$Version-examples.zip"), $Work); }
if (-not (Test-Path (Join-Path $ex 'calibration.json'))) { throw "examples not found in $ex" }

if ($Mode -eq 'Portable') {
    $dir = Join-Path $Work 'portable'; if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    Write-Host '== unzip portable'; [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $Release "QwlsiApp-$Version-portable.zip"), $dir)
    $exe = Join-Path $dir "QwlsiApp-$Version\QwlsiApp.exe"
} else {
    $dir = Join-Path $Work 'installed'; if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    Write-Host '== silent per-user install'
    $setup = Join-Path $Release "QwlsiApp-$Version-setup.exe"; $ilog = Join-Path $Work 'install.log'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ip = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=$dir", '/TASKS=desktopicon', "/LOG=$ilog" -Wait -PassThru
    Write-Host ("   installer exit code {0} after {1:N0} s" -f $ip.ExitCode, $sw.Elapsed.TotalSeconds)
    $exe = Join-Path $dir 'QwlsiApp.exe'
    $desk = [Environment]::GetFolderPath('Desktop')
    $lnk = @("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\QWLSI wavefront\QWLSI single-frame wavefront.lnk", (Join-Path $desk 'QWLSI single-frame wavefront.lnk'))
    foreach ($l in $lnk) { Write-Host ("   shortcut {0}: {1}" -f $l, (Test-Path $l)) }
    $reg = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | Where-Object { (Get-ItemProperty $_.PSPath).DisplayName -like 'QWLSI*' }
    Write-Host ("   uninstall registry entry: {0}" -f [bool]$reg)
}
if (-not (Test-Path $exe)) { throw "exe not found: $exe" }
Write-Host ("== exe: {0}  version {1}" -f $exe, (Get-Item $exe).VersionInfo.ProductVersion)

$out = Join-Path $Work ("run_{0}{1}" -f $Mode, $(if ($MklIsa) { '_' + $MklIsa } else { '' })); if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Write-Host "== smoke test (minimal environment$(if ($MklIsa) { ', MKL_ENABLE_INSTRUCTIONS=' + $MklIsa }))"
$frames = (Join-Path $ex 'frames\concave_1_img.tif') + ';' + (Join-Path $ex 'frames\convex_2_img.tif')
$r = Run-Clean $exe @('--selftest', $out, '--file', $frames, '--ref', (Join-Path $ex 'ref.tif'), '--calib', (Join-Path $ex 'calibration.json'), '--flat', (Join-Path $ex 'ref.tif'))
Write-Host ("   exited={0} code={1} after {2:N0} s" -f $r.Exited, $r.Code, $r.Seconds)
$log = Join-Path $out 'selftest.log'
if (Test-Path $log) { Get-Content $log | Where-Object { $_ -match '^without reference|^with reference|^export|^zernike|^3D|crash report|SELFTEST|peak memory|MEM ' } | ForEach-Object { '   ' + $_.Substring(0, [Math]::Min(230, $_.Length)) } } else { Write-Host '   NO selftest.log' }
$al = Get-ChildItem (Join-Path $out 'appdata\logs') -Filter 'qwlsi-*.log' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($al) { Write-Host '   --- processing lines of the app log:'; Get-Content $al.FullName | Where-Object { $_ -match 'processed |ERROR|batch start|export:' } | ForEach-Object { '   ' + $_.Substring(0, [Math]::Min(250, $_.Length)) } }
$shots = (Get-ChildItem $out -Filter '*.png' -ErrorAction SilentlyContinue | Measure-Object).Count; Write-Host "   screenshots written: $shots"

if ($Mode -eq 'Installer') {
    Write-Host '== uninstall'
    $un = Join-Path $dir 'unins000.exe'; $up = Start-Process $un -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    Start-Sleep 3
    Write-Host ("   uninstaller exit code {0}; install folder removed: {1}" -f $up.ExitCode, -not (Test-Path $dir))
    foreach ($l in $lnk) { Write-Host ("   shortcut removed ({0}): {1}" -f $l, -not (Test-Path $l)) }
    $reg = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue | Where-Object { (Get-ItemProperty $_.PSPath).DisplayName -like 'QWLSI*' }
    Write-Host ("   uninstall registry entry removed: {0}" -f (-not [bool]$reg))
}
