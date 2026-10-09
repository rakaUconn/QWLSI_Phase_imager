# Test report — QWLSI single-frame wavefront 1.1.0 (2026-10-09)

## Unit / integration tests (`dotnet test src/Qwlsi.Tests -c Release`, bundled ffmpeg found through `QWLSI_FFMPEG`)
150 tests, 150 passed, 0 failed, 5 min 10 s. New in 1.1.0: `VideoSinkTests` (PNG / GIF / AVI / MP4 sinks, MP4 -> AVI fallback, odd sizes rejected, cancel deletes partial output), `VideoRenderTests` (output sizes per preset, 1-4 panel layouts, range modes, units, overlays, strip plot, filtered + removed == original, per-frame vs common circle, software 3-D rasteriser incl. closed 360 degree loop, turntable, blending, job cancel / error), `VideoEndToEndTests` (5 real frames -> real encoders; MP4 decoded with ffmpeg: 5 frames, 1280x720; GIF 5 frames via System.Drawing; AVI header 5 frames; PNG sequence 5 files; blending 13 frames, turntable 20 frames, MP4 without ffmpeg -> AVI), `ZernikeTableTests` (table rows equal the single-frame Zernike results, CSV contents, mean / std, common circle, TryGetMask, one frame in memory, cancellation, export-all, astigmatism axis).

## Scripted application test (`QwlsiApp.exe --selftest`, real window, 5 real frames, release build)
* Zernike all frames: 5 rows + mean / std, 11 numeric columns; sorting ascending / descending; clicked row shows original / removed / filtered maps and selects the frame; common circle (centre 749.3, 820.7 px, radius 512.2 px, 824,243 px intersection) identical in every row; export all filtered maps: 11 files; table build 1.1 s for 5 cached frames (18 s the first time, because frames missing from the cache were recomputed).
* Video, 5 frames, cold start (analysis pass included): 4 panels + strip plot MP4 1080p 2.2 s (peak working set during the render 563 MB); 1 panel OPD MP4 1080p 0.6 s (440 MB); GIF 720p 0.5 s (320 MB); PNG sequence 720p 0.6 s (391 MB); turntable 60 frames MP4 720p 1.8 s. H.264 encoder on this PC: `h264_mf`. Whole-session peak working set 1055 MB (includes the 5 reconstructions kept by the batch).

## Clean-location test (`tools/clean-test.ps1 -Mode Portable`: portable zip unpacked in `C:\QwlsiCleanTest`, PATH reduced to Windows)
Exit code 0 after 135 s; the bundled `ffmpeg\ffmpeg.exe` was found next to the program and produced the MP4 files; all earlier checks (offline reconstruction, with reference, Zernike exports, 3-D hardware and WARP with mouse input, About, crash report) unchanged. The installer mode was not re-run for 1.1.0.

## Not verified
Playback of the GIF / AVI / MP4 in third-party players, the GPU encoders (`h264_nvenc`, libopenh264) through the sink in the release build, other Windows editions (an N edition without Media Foundation would use another encoder or the AVI fallback), the 1.1.0 installer in silent-install mode.
