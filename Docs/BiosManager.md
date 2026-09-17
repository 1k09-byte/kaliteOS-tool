# BIOS Manager (SCEWIN) — file grammar & parser notes

The BIOS Manager page (`Pages/BiosManagerPage.xaml`) auto-exports the live
BIOS settings every time it opens by running the bundled SCEWIN tool
(`Assets/scewin`, deployed to `scewin\` next to the app exe):

```
SCEWIN_64.exe /O /S BIOSSettings.txt /SD Dupes.txt
```

The dump is cached at `%LOCALAPPDATA%\kaliteConfig\BIOSSettings.txt` and
replayed instantly on the next page open while a fresh export runs. The
"Export & load" toolbar button re-runs the tool at any time, and a manual
"Load dump" picker remains for opening a dump file by hand.

An edited file can be fed back with `SCEWIN_64.exe /i /s modified.txt`
(the bundled `Import.bat` does exactly this).

## Assumed grammar

The parser (`Services/ScewinParser.cs`) is deliberately tolerant — it splits
key/value pairs on the **first `=` per line** and trims, so `=` alignment,
extra whitespace, CRLF vs LF, and missing optional fields are all accepted.
It assumes the file is organized as:

```
//  SCEWIN_64 Rev ...  (comments/banner lines — preserved verbatim)
//
!BIOS Setting Get/Set utility output
-----------------------------------------------------

----- Advanced -----                                        ← section header (optional)

Setup Question          = CPU Ratio Limit                   ← item block start
Help String             = Sets the maximum CPU ratio
Token                   = 0x0058
Offset                  = 0x00
Width                   = 01
BIOS Default            = 0x28
Options                 = 0x24=36, 0x28=40, 0x2C=44         ← "token=label" pairs or bare tokens
Value                   = 0x28                              ← the only line the exporter rewrites
-----------------------------------------------------
```

### Recognition rules

| Element | Rule |
|---|---|
| Item block | Starts at a line whose key (text before the first `=`) is `Setup Question` (alias: `Question`). |
| Block end | A separator line (only `-`, `=`, or `~` characters), a section header, the next item, or EOF. |
| Known keys | `Setup Question`, `Help String`, `Token`, `Offset`, `Width`, `BIOS Default` (alias: `Default`), `Options`, `Value`. Unknown keys are kept verbatim in the raw block, never dropped. |
| Section headers | Lines matching `----- Title -----`, or a full path like `----- Advanced ----- CPU Configuration -----` — the dash-run-separated tokens are the absolute menu path (a level per token). |
| Per-item path override | A `Menu = Advanced\CPU Configuration` line inside an item overrides the header path (`\`, `/`, `\|`, ` > ` all accepted as separators). |
| Options styles | `0x24=36, 0x28=40` (hex tokens with labels), `Disable, Enable` (bare tokens), or `0x50..0xB4` (a single numeric range → free-form numeric editor with bounds). |

If a dump uses a different header style (e.g. no dash-wrapped titles), the
items still parse and land under **All settings** — nothing is lost; the
only cosmetic difference is a flat tree. Adjust `TryParseHeader` in
`ScewinParser.cs` to recognize your header format.

### Fidelity guarantee

Every line of the imported file is stored verbatim in a segment stream
(`ScewinDocument.Segments`). Export concatenates the stream, rewriting only
the `Value` line of modified items (spacing before `=` is preserved).
**Import → export with zero edits is byte-identical**; an edited file differs
from the original only on the edited `Value` lines. Unit tests assert this
(`kaliteConfig.Tests`).

## Editing & validation

- Enumerated items (an `Options` list with tokens/labels) render a `ComboBox`
  restricted to the legal tokens — an out-of-list value is unrepresentable.
- Free/range items render a validated `TextBox` (hex `0x…` or decimal);
  invalid input shows an inline error state.
- On export the review dialog lists every change (Name → Old → New) and
  **blocks export** while any value is invalid. Warnings are non-blocking,
  and nothing invalid is ever written silently.

## Elevated file pickers

This app runs `requireAdministrator`. The brokered WinUI 3 pickers
(`FileOpenPicker`/`FileSavePicker`) throw `E_ACCESSDENIED` in elevated
processes, so `BiosManagerViewModel` uses the standard WinUI desktop pattern
(`InitializeWithWindow` + window handle) **first** and automatically falls
back to the Win32 `IFileDialog` COM dialog (`Services/Win32FilePicker.cs`),
which works elevated.

## Files

- `Models/BiosSetting.cs` — item model + option/range parsing + validation
- `Models/BiosMenuSection.cs` — menu tree node (with subtree counts)
- `Models/ScewinDocument.cs` — verbatim segment stream + parsed items
- `Services/ScewinParser.cs` — text → model (pure, unit-testable)
- `Services/ScewinExporter.cs` — model → text (byte-faithful)
- `ViewModels/BiosSettingRow.cs` — observable row wrapper (modified/invalid state)
- `ViewModels/BiosManagerViewModel.cs` — page state & commands
- `Pages/BiosManagerPage.xaml(.cs)` — three-pane responsive UI
- `kaliteConfig.Tests/` — parser/exporter round-trip tests
