# QwlsiApp — project context for Claude Code

Build a standalone Windows x64 desktop app (C#, .NET 8, WPF) that reconstructs the wavefront (OPD + complex field)
from a single raw QWLSI (quadri-wave lateral shearing) frame. It is a C# port of the validated Python/MATLAB code in
`..\recon_single_frame\` (source of truth: `qwlsi_single_frame.py`; MATLAB port: `qwlsi_single_frame.m`; method notes: `..\recon_single_frame\README.md`).

READ `HANDOFF.md` FIRST — it is the full spec, task list and acceptance tests.

Rules
- Do not change the algorithm silently. Any deviation from the Python reference must be justified by a measured comparison on the real frames.
- Work in phases (HANDOFF.md §9). Finish and verify Phase 0 before starting Phase 1. Commit after every working step.
- Never edit files in `..\recon_single_frame\` or the raw `.tif` data; read only. Write only inside `QwlsiApp\`.
- Use float32 + preallocated buffers on hot paths (the frame is 4512x4512; memory matters).
- Keep `Qwlsi.Core` free of UI/camera dependencies so it can be unit-tested.
