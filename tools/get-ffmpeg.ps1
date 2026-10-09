<#
  Downloads the pinned LGPL build of ffmpeg (BtbN FFmpeg-Builds, win64, static, ffmpeg 8.1.3) into third_party\ffmpeg\ (gitignored): ffmpeg.exe, LICENSE.txt, VERSION.txt.
  The archive is verified against a pinned SHA-256 before anything is extracted; only ffmpeg.exe and LICENSE.txt are taken, from a fresh temp folder; the only thing ever executed is ffmpeg.exe -version / -buildconf.
  Usage:  powershell -ExecutionPolicy Bypass -File tools\get-ffmpeg.ps1 [-Force]
#>
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root 'third_party\ffmpeg'
$tag  = 'autobuild-2026-10-09-14-16'
$name = 'ffmpeg-n8.1.3-16-ge0a878dd70-win64-lgpl-8.1.zip'
$url  = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$tag/$name"
$sha  = '346fd19f3afe98a8606da4fc2984c8b411297734dba101cd91ec961fa8b2f875'

if (-not $Force -and (Test-Path (Join-Path $dest 'ffmpeg.exe')) -and (Test-Path (Join-Path $dest 'LICENSE.txt')) -and (Test-Path (Join-Path $dest 'VERSION.txt'))) { Write-Host "ffmpeg already present: $dest (use -Force to download again)"; return }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('qwlsi_ffmpeg_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp $name
    Write-Host "downloading $url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $got = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($got -ne $sha) { throw "SHA-256 mismatch for $name`n  expected $sha`n  got      $got`nNothing was extracted." }
    Write-Host "SHA-256 ok: $got"

    $x = Join-Path $tmp 'x'; New-Item -ItemType Directory -Force $x | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $za = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($e in $za.Entries) {
            if ($e.FullName -match '/bin/ffmpeg\.exe$' -or $e.FullName -match '^[^/]+/LICENSE\.txt$') {
                $out = Join-Path $x ([IO.Path]::GetFileName($e.FullName)); [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $out, $true)
            }
        }
    } finally { $za.Dispose() }
    $exe = Join-Path $x 'ffmpeg.exe'; $lic = Join-Path $x 'LICENSE.txt'
    if (-not (Test-Path $exe) -or -not (Test-Path $lic)) { throw 'ffmpeg.exe or LICENSE.txt not found in the archive' }

    $ver  = (& $exe -hide_banner -version | Select-Object -First 1)
    $conf = (& $exe -hide_banner -buildconf) -join "`n"
    if ($conf -notmatch '--enable-version3') { throw 'unexpected build: --enable-version3 missing' }
    if ($conf -match '--enable-gpl' -or $conf -match '--enable-nonfree') { throw 'this build is GPL / nonfree: refusing to bundle it (the release must be LGPL)' }
    $flags = ($conf -split "\s+" | Where-Object { $_ -match '^--(enable-version3|enable-gpl|enable-nonfree|disable-libx264|disable-libx265|enable-libopenh264|enable-amf|enable-ffnvcodec|enable-libvpl)$' }) -join ' '

    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $exe (Join-Path $dest 'ffmpeg.exe') -Force; Copy-Item $lic (Join-Path $dest 'LICENSE.txt') -Force
    @(
        "FFmpeg (LGPL build) bundled with QWLSI single-frame wavefront",
        "build tag : $tag (BtbN/FFmpeg-Builds, win64 lgpl static)",
        "archive   : $url",
        "sha256    : $sha",
        "version   : $ver",
        "licence   : LGPL v3 (see LICENSE.txt); configure flags that matter: $flags",
        "source    : https://ffmpeg.org/  https://github.com/FFmpeg/FFmpeg (commit e0a878dd70)  https://github.com/BtbN/FFmpeg-Builds (tag $tag)",
        "obtained  : $((Get-Date).ToString('s')) by tools\get-ffmpeg.ps1"
    ) | Set-Content (Join-Path $dest 'VERSION.txt') -Encoding UTF8
    Write-Host "installed $ver -> $dest"
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
