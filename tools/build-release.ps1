<#
  Final release build: publish (self-contained win-x64) -> trim unused MKL files -> docs -> portable zip -> Inno Setup installer -> examples zip -> checksums.
  Usage:  powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 [-Version 1.0.1] [-SkipInstaller]
  Output: dist\release\QwlsiApp-<v>-setup.exe, QwlsiApp-<v>-portable.zip, QwlsiApp-<v>-examples.zip, SHA256SUMS.txt
#>
param([string]$Version = "1.0.1", [switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$data = Split-Path $root -Parent                       # ..\ holds the sample frames and the Python reference results
$pub = Join-Path $root 'dist\publish'; $rel = Join-Path $root 'dist\release'; $stage = Join-Path $root 'dist\stage'
Set-Location $root

# the version in Directory.Build.props must match
$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
if ($props -notmatch "<Version>$([regex]::Escape($Version))</Version>") { throw "Directory.Build.props does not say $Version" }

Remove-Item $pub, $rel, $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $rel, $stage | Out-Null

Write-Host '== publish'
dotnet publish src\Qwlsi.App -c Release -r win-x64 --self-contained -p:DebugType=none -p:DebugSymbols=false -o $pub
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Host '== trim unused MKL / TBB / debug files (FFT needs only mkl_rt, mkl_core, the Intel-threading layer and the CPU kernels)'
$drop = @('mkl_tbb_thread.3.dll', 'mkl_sequential.3.dll', 'mkl_vml_*.dll', 'tbb*.dll', 'libiomp5md.pdb', 'libiomp5md_db.dll', '*.pdb')
foreach ($pat in $drop) { Get-ChildItem $pub -Filter $pat -File -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "   removed $($_.Name)"; Remove-Item $_.FullName } }

Write-Host '== docs'
if (-not (Test-Path (Join-Path $root 'THIRD-PARTY-NOTICES.txt'))) { python tools\gen_notices.py }
foreach ($f in 'USER-GUIDE.md', 'RELEASE-NOTES.md', 'THIRD-PARTY-NOTICES.txt') { Copy-Item (Join-Path $root $f) $pub }

Write-Host '== content checks'
$bad = Get-ChildItem $pub -Recurse -File | Where-Object { $_.Name -match 'xiapi|ximea|xiApi|fftw' }
if ($bad) { throw "forbidden files in the release: $($bad.Name -join ', ')" }
$exe = Join-Path $pub 'QwlsiApp.exe'; $ver = (Get-Item $exe).VersionInfo
if ($ver.ProductVersion -notlike "$Version*") { throw "exe version is $($ver.ProductVersion), expected $Version" }
Write-Host ("   QwlsiApp.exe version {0}; {1} files; {2:N0} MB" -f $ver.ProductVersion, (Get-ChildItem $pub -Recurse -File).Count, ((Get-ChildItem $pub -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

Write-Host '== portable zip'
$pz = Join-Path $stage "QwlsiApp-$Version"; Copy-Item $pub $pz -Recurse
Add-Type -AssemblyName System.IO.Compression.FileSystem
$portable = Join-Path $rel "QwlsiApp-$Version-portable.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($pz, $portable, [IO.Compression.CompressionLevel]::Optimal, $true)

if (-not $SkipInstaller) {
    Write-Host '== installer'
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found' }
    & $iscc "/DAppVer=$Version" "/DSrcDir=$pub" "/DOutDir=$rel" installer\QwlsiApp.iss
    if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
}

Write-Host '== examples zip'
$ex = Join-Path $stage 'examples'; New-Item -ItemType Directory -Force "$ex\frames", "$ex\golden" | Out-Null
Copy-Item (Join-Path $root 'installer\examples-README.md') "$ex\README.md"
Copy-Item (Join-Path $data 'recon_single_frame\calibration.json') $ex
Copy-Item (Join-Path $data 'recon_single_frame\results\ref_OPD_m.npy') $ex
Copy-Item (Join-Path $data 'ref.tif') $ex
foreach ($n in 'concave_1_img', 'concave_2_img', 'concave_1-5_img', 'convex_1_img', 'convex_2_img', 'convex_1-5_img') {
    Copy-Item (Join-Path $data "$n.tif") "$ex\frames"; Copy-Item (Join-Path $data "recon_single_frame\results\${n}_OPD_m.npy") "$ex\golden"
}
$exzip = Join-Path $rel "QwlsiApp-$Version-examples.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($ex, $exzip, [IO.Compression.CompressionLevel]::Optimal, $true)

Write-Host '== checksums'
Get-ChildItem $rel -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } | Set-Content (Join-Path $rel 'SHA256SUMS.txt') -Encoding ascii
Get-ChildItem $rel -File | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
