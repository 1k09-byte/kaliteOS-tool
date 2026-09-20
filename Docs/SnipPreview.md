# Snip Phase 3 — Large Preview (Win2D) Verification Record

Machine: LITEOS-BETA3 · date 2026-09-20 · build `Debug|x64` (WASDK 2.4.0, Win2D 1.4.0, net10.0)

Harnesses:

- `kaliteconfig.Tests` — 91 tests, includes `SnipPreviewViewportTests` (zoom/pan/DPI/policy).
- `tools/SnipVerify` — 71 checks, includes the Phase 3 section: viewport policy plus a **real disk
  round-trip** of the progressive load (512 px tier → full-resolution pixels) against the actual
  `SnipPreviewService` / `SnipThumbnailService`.

## Scope

Phase 3 builds ONE large-preview pipeline and uses it in two places:

| Piece | Where |
|---|---|
| `Models/SnipPreviewViewport.cs` | pure zoom/pan/DPI math + interpolation and backing-store policy |
| `Services/SnipPreviewService.cs` | 512 px placeholder tier + full-resolution pixel decode (device-agnostic) |
| `Controls/SnipPreviewView.xaml(.cs)` | Win2D `CanvasControl`: progressive load, crossfade, zoom/pan chrome |
| `Windows/SnipViewerWindow.xaml(.cs)` | full viewer window (toolbar, keyboard, window sizing) |
| `Pages/SnipPage.xaml` details panel | hosts the same control (double-tap detaches into the viewer) |

## Results

| Spec item | Result |
|---|---|
| Progressive: 512-tier placeholder, then full resolution | PASS — `LoadPlaceholderAsync` returns the 512 tier (512x384 from a 1200x900 source, smallest covering tier, never upscaled); full-res pixels decoded separately and crossfaded in over 220 ms |
| Zoom/pan with DPI-correct 100% | PASS — at 100% and display scales 1.0/1.5/2.0 the image occupies exactly `image_px` physical pixels (`DisplayWidthDip * scale == image_px`); scale changes keep 100% pixel-accurate |
| Interpolation by zoom level | PASS — ≥2x NearestNeighbor, ~1:1 Linear, ≥0.5 Cubic, below that MultiSampleLinear; the placeholder is never drawn with NearestNeighbor |
| CanvasVirtualBitmap for huge images | PASS (policy + load path) — `>8192 px` on the longest edge or `>32 Mpx` routes to `CanvasVirtualBitmap`; only the visible source rectangle is drawn, so only those regions are realized. 8000x8000 → virtual, 1920x1080 / 8000x4000 → one direct bitmap |
| Used by the details panel | PASS — build-clean; panel preview reserves a fixed 220 px frame (no zero-height collapse) |
| Full viewer | PASS — build-clean; Fit / 100% / ± / Ctrl+0 / Ctrl+1 / Ctrl+± / Esc, window opens sized to the snip, clamped to the work area |
| Existing surfaces unchanged | PASS — build succeeds, 91 unit tests + 71 harness checks pass |

## Findings that changed code

1. **Buttons rendered their labels as tofu boxes.** `Content="&#xE8C8; Copy"` with
   `FontFamily="{ThemeResource SymbolThemeFontFamily}"` puts the *whole string* — icon codepoint
   and text — through the icon font, so "Copy", "Edit", "Pin" and "Favorites only" drew as boxes
   (visible in the reported screenshot). Fixed by making the glyph a child `FontIcon` and the label
   a `TextBlock`, which is also what the rest of the app already does.

2. **The preview only appeared after taking a screenshot.** Two independent defects produced this
   one symptom, and both are now fixed.

   **(a) A pixel-less `BitmapImage`.** `SnipGalleryService` created a `BitmapImage`, awaited
   `SetSourceAsync`, then disposed the stream — but `SetSourceAsync` returns once the stream has
   been *read*, while the decode completes later on a worker thread. Callers therefore received a
   pixel-less `BitmapImage`: the `Image` element had no natural size, collapsed to 0x0 inside the
   details panel's auto-sized grid, and stayed invisible until something outside the app forced a
   fresh layout pass (taking a screenshot re-activating the window did exactly that).
   `LoadThumbnailResultAsync` now waits for `ImageOpened` (or a real `ImageFailed`) with the stream
   still open, and fails with the actual reason after an 8 s timeout — a card can no longer show a
   silent blank. The details panel additionally no longer depends on a gallery card having been
   realized for its preview.

   **(b) A card's visibility depended on a storyboard clock.** The card reveal was a 150 ms
   `DoubleAnimation` fading the thumbnail in from `Opacity = 0`, plus a `RepeatBehavior.Forever`
   storyboard pulsing the skeleton. Storyboarded opacity is advanced by the render clock, which
   stops while the window is not composing — minimised, occluded, or sitting behind the snipping
   overlay. A fade that started in that window froze part-way and **held** the image at the opacity
   it had reached, so the card kept showing skeleton and the pixels only came back when something
   put the window back into rendering (taking a screenshot did exactly that). The reveal is now a
   plain `Opacity = 1` assignment and the skeleton pulse is a `DispatcherQueueTimer`, so nothing
   about a card being correct depends on an animation clock that may never tick. A realised card is
   also pushed its state directly (`ApplyCardVisuals` in `HookCard`), so a card that finished
   decoding at an awkward moment can never sit on screen out of step with its `SnipCardState`.

   Measured on the live window with `tools/repaint-probe.ps1`: the card region read mean 51.6 /
   sd 22.9 with a skeleton, and 73.1 / 17.5 — the actual thumbnail — after a forced repaint, i.e.
   the frame on screen was stale by a whole image, and only a nudge — scroll, view switch or
   minimise/restore — put the pixels on screen. After the fix the thumbnail lands within the same second as the load
   (`tools/screen-ascii.ps1` renders the card as text: control buttons at the top-right, decoded
   pixels filling the tile), and the Win2D details preview was confirmed to draw real full-resolution
   colour (e.g. `#174064`, `#E5BD91` at x=1750) rather than a flat placeholder.

3. **The details panel's Retry / Remove-from-gallery buttons were dead.** They were wired to the
   card handlers, which read `DataContext` off the button — the panel's DataContext is the view
   model, so neither pattern matched. They now act on `SelectedGalleryItem` explicitly (and the
   Retry path re-runs the preview load).

4. **`tools/SnipVerify` did not compile at all** (pre-existing: `SnipGalleryService` references
   `SnipThumbnailService`, which the harness did not include). The harness now compiles the real
   thumbnail + preview services with a tiny `BitmapImage` stand-in, so the Phase 3 pipeline is
   verified against the real disk cache instead of a fake.

## Diagnostic tooling added (dev-only, Windows)

These three scripts are what made the bug above visible without human eyes — a terminal agent
cannot read a screenshot, but it can read a luminance map and pixel values:

- `tools/uia-dump.ps1` — dumps the live window's UI Automation tree with control types and
  bounding rectangles. An element with an empty (all-NaN) rect exists in the tree but was never
  laid out; `-Invoke`/`-Click` navigate the app, `-Probe` hit-tests points.
- `tools/screen-ascii.ps1` — renders a screen region as ASCII luminance art (contrast-normalised,
  high-local-contrast cells marked) plus a `-Scanline` mode that prints raw pixel colours down a
  column. This is how "flat fill" vs "decoded image" was settled.
- `tools/repaint-probe.ps1` — measures a region, nudges the window one pixel, then minimises and
  restores it, reporting whether the pixels moved. A change proves the on-screen frame was stale
  rather than merely empty.
- `tools/make-preview-fixtures.ps1` — generates the checker/8K/huge test snips used above.

**Rule of thumb for this app:** nothing that decides whether async-loaded content is *visible* may
depend on a `Storyboard`, and any card state that can complete while the window is not composing
must be pushed to its container in code.

## Manual walkthrough still required (needs a GPU + window)

- [ ] Crossfade: selecting a large snip shows the 512 px preview first, then the full image fades in.
- [ ] 100% on a 150% monitor: `Ctrl+1` in the viewer, then compare a UI corner of the snip against the screen — the pixels must line up exactly.
- [ ] Huge image: open a `>32 Mpx` capture in the viewer, confirm memory stays flat while panning (regions realized on demand).
- [ ] Interpolation: zoom past 200% and confirm individual image pixels are crisp squares, not blurred.
- [ ] Details panel: double-tap the preview opens the viewer; plain wheel still scrolls the panel; Ctrl+wheel zooms.
- [x] Recent Snips + Gallery cards: thumbnails appear on their own, no screenshot/resize needed.
- [x] Details panel preview: selecting a snip draws the real image (not a placeholder), and the
      Copy / Edit / View buttons render as icon + label instead of tofu boxes.
- [ ] Device loss: change the display resolution/scale while the viewer is open and confirm the preview rebuilds.
