# FastBlur

Auto-blur sensitive info in videos — links, passwords, emails, API keys,
crypto addresses — for YouTube tutorials, without manual work in an editor.

FastBlur is a native Windows desktop app (WPF on .NET 8). It is a C# port
of the Go app [BlurCut](https://github.com/sahfar363/blurcut) (v0.2.7),
rebuilt the way C# fits best: a real desktop window instead of a web UI,
no local web server, no browser tab. Everything runs offline on your PC —
no API, no internet at runtime.

## Install (Windows)

1. Download `fastblur-windows-amd64.zip` from the
   [latest release](https://github.com/sahandfarshad/fastblur/releases/latest).
2. Unzip it anywhere, e.g. `C:\fastblur`.
3. Double-click `FastBlur.exe`.

The zip already contains everything: `FastBlur.exe` plus `tools/ffmpeg.exe`,
`tools/ffprobe.exe`, `tools/tesseract.exe` (+ its DLLs) and
`tools/tessdata/eng.traineddata`. Nothing else to install.

## How to use

1. **Open a video** — drag a `.mp4`, `.mov` or `.mkv` onto the window,
   or use *Open video…*. Long videos (30 minutes and more) work: frames
   are streamed, never loaded whole into memory.
2. **Auto-scan** — press *Auto-scan*. FastBlur extracts frames, reads them
   with local Tesseract OCR (frames are upscaled 2x first, so small screen
   text reads correctly), and finds links, passwords, emails, API keys and
   crypto addresses. Scan progress shows frame N/M. A scan **merges** with
   your manual boxes — it never deletes them.
3. **“What does the scan see here?”** — shows the exact words Tesseract
   read at the current frame, so you can see why a scan found (or missed)
   something.
4. **Draw a box** — click *Draw a box*, drag a rectangle on the preview.
   Drawing turns itself off after one box, so Play always works (clicks
   reach the video, never a stuck overlay). Double-click a region in the
   list to jump to it; select one to edit its start/end times.
5. **Export** — press *Export blurred video*. The output is always
   `.mp4`, even when the source is `.mkv` or `.mov`, and is verified to
   be a real playable video before it is offered as done. Audio is kept
   (re-encoded to AAC when the source codec can't go into mp4).

Jobs are saved on disk (`%LOCALAPPDATA%\FastBlur`) and restored when you
reopen the app. A corrupt or empty export from an earlier run is deleted
on restore instead of being offered as a download.

## What it detects

| Label | What matches |
|---|---|
| url | `https://…`, `www.…`, `domain.com` etc. |
| email | `name@example.com` |
| password | `password`, `passwd`, `pwd` — including a bare label with no `:` or `=` after it |
| apikey | `api_key`, `AKIA…`, `ghp_…`, `sk-…`, `-----BEGIN … PRIVATE KEY-----`, `secret:` / `token=` |
| crypto | Bitcoin `bc1…` / `1…` / `3…`, Ethereum `0x` + 40 hex |
| keyword | your own words (Settings) |
| possible-key | long random-looking tokens, letters + digits — **opt-in, default OFF** (can be noisy) |

Toggles for each pattern live in Scan settings, along with the scan
interval (0.5–2 s between scanned frames) and blur strength (10–30).

## Tools

FastBlur shells out to `ffmpeg` (probe, frames, blur, preview) and
`tesseract` (OCR). It looks for them in this order: a path you saved in
the Tools section, the `FASTBLUR_FFMPEG` / `FASTBLUR_TESSERACT`
environment variables, next to `FastBlur.exe`, in `tools/` or `bin/`,
the current folder, `PATH`, then the usual Windows install folders.
The Tools section shows what was found (green = found) and what was
searched, lets you set paths by hand, and can download ffmpeg (current
gyan.dev release essentials build) and the Tesseract installer (UB-Mannheim 5.5.3)
with one click.

## Command line

The same app also works headless, for scripts:

```
FastBlur.exe scan <video> --out detections.json
FastBlur.exe blur <video> --regions regions.json --out out.mp4
FastBlur.exe --help
FastBlur.exe --version
```

## Build from source

Windows (full app, needs .NET 8 SDK):

```
dotnet build src/FastBlur/FastBlur.csproj -c Release
dotnet publish src/FastBlur/FastBlur.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Linux / macOS (core library + tests only — WPF is Windows-only):

```
dotnet test tests/FastBlur.Core.Tests/FastBlur.Core.Tests.csproj
```

If `dotnet test` aborts on your machine with "failed to connect to
testhost process" (some sandboxes intercept .NET loopback TCP — the
vstest testhost handshake never completes, even though the tests are
fine), run the same suite in-process instead:

```
dotnet run --project tests/FastBlur.TestRunner -c Release
```

All detection, OCR, ffmpeg and job logic lives in `src/FastBlur.Core`
(a plain `net8.0` class library) so it builds and tests anywhere; only
the thin WPF shell in `src/FastBlur` needs Windows.

## Project layout

```
src/FastBlur.Core/     all logic — no UI, builds on Linux
  Detection/           OCR word grouping, sensitive-text patterns, region geometry
  Ocr/                 tesseract runner, frame scanning, OCR preview
  Ffmpeg/              tool detection, probing, blur filter, export, preview, downloads
  Jobs/                job persistence (jobs.json)
  Config/              settings persistence (config.json)
  Cli/                 headless scan/blur commands
src/FastBlur/          WPF app — MainWindow, ViewModel, drawing overlay, playback
tests/FastBlur.Core.Tests/  xUnit tests (unit + real-ffmpeg integration)
tests/FastBlur.TestRunner/  in-process xUnit runner (fallback where `dotnet test` can't reach its testhost)
.github/workflows/     CI (core tests on Linux, WPF build on Windows) + release zip
scripts/               bundle-tesseract.py (extracts Tesseract from the UB-Mannheim installer)
```

## Version

**0.1.0** — first C# release. Feature parity with BlurCut (Go) v0.2.7,
including the fixes learned the hard way there: odd-dimension videos
breaking libx264, blur radius failing small regions, the overlay swallowing
Play clicks, scans replacing manual boxes, and exports named with the
source extension that players refused.

See `THIRD-PARTY-NOTICES.md` for the licences of the bundled ffmpeg and
Tesseract builds.
