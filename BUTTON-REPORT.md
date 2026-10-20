# Button reset: final report

Branch: `style/stock-buttons` (from `master` @ cac1385). Progress is tracked in `BUTTON-PROGRESS.md`, and the control inventory is in `BUTTON-INVENTORY.md`.

## Result

- Every button, dialog, and button lookalike in scope now uses stock WinUI 3 controls, with at most one accent button per view.
- The custom button styles are gone: `KaliteButton`, `KaliteSubtleButton`, `KaliteIconButton`, the `KaliteButtonFill`/`KaliteButtonStroke` brushes, and their hover/press Storyboards. A grep for these names returns no matches.
- Paired buttons merged: benchmark capture (Start/Stop), speed test (Start/Cancel).
- Custom hover/press animations removed. The installer card style that carried opacity/scale transitions is deleted.
- Lookalikes converted to stock `Button`/`HyperlinkButton`: the BIOS value pill, the thread tuner row chevron, the snip status-bar link, and the snip toast tap target.
- Dialogs: every button-opened dialog is a stock `ContentDialog` or `Flyout`.
- Nav (`SideNavResources.xaml`, the nav rail), window backdrop, and title bar are unchanged. Nav gets its own frozen copy of the old button look, so it does not change with the global reset.
- Build: `dotnet build src/kaliteConfig/kaliteConfig.csproj` passes with 0 warnings and 0 errors. This matches the baseline. No TODO or FIXME was added on the branch.

## Changes by step

| Step | Commit(s) | Summary |
|---|---|---|
| 0 | (branch and baseline) | Windows App SDK 2.4.0; net10.0-windows10.0.22621.0 |
| 1 | 05a9ab4 | Inventory of buttons, animations, and lookalikes |
| 2 | (nav guard) | Frozen nav button resources in `Themes/SideNavResources.xaml` |
| 3 | (global layer) | Removed the custom button styles and brushes from `KaliteTheme.xaml` and `FontPreference.cs` |
| 4 | 9e5cdfb, 557e52d, 6925b81 | One accent per view; installer card style removed; BIOS pill and tuner chevron to stock |
| 5 | d1249ef, 05a9ab4 | Benchmark capture and speed test merged into one toggle each |
| 6 | d1249ef | Hover/press animations removed; kept cases logged below |
| 7 | 6925b81, f94cbc0 | Lookalikes converted to stock buttons |
| 8 | (dialogs audited) | Dialogs confirmed as stock `ContentDialog`/`Flyout`; overrides logged below |

## Merged pairs

- **Benchmark capture:** Start capture and Stop are one `ToggleCaptureCommand`. The label and glyph follow `IsCapturing`. The accent is dropped, because one button cannot hold an accent in both states.
- **Speed test:** Start and Cancel are one `SpeedToggle_Click`. The label follows `SpeedRunning`.
- **Thread list Suspend/Resume:** Already one button whose label follows the state. No change.

## Pairs checked and not merged

- **PackageRow Action + Cancel:** both stay visible while an operation runs, so they are not mutually exclusive.
- **Apply/Cancel dialog pairs:** standard dialog buttons.
- **ThreadTuner flyout Suspend/Resume:** separate menu items, not a button pair.

## BUTTON-REVIEW items

| Location | Decision |
|---|---|
| `MainWindow.xaml:708` | The KaliteOS gate stays a hand-built `Border` with a stock Exit button. It covers the window from the first frame, before code-behind runs, and a `ContentDialog` cannot be shown that early. |
| `Pages/BenchmarkPage.xaml:126` | Capture toggle has no accent. The merged button cannot hold one accent in both states. |
| `Pages/BenchmarkPage.xaml.cs:64` | Benchmark cards keep `Border` + `PointerPressed`. They hold nested controls, so a `Button` would nest interactive children. |
| `Pages/NetworkPage.xaml:466` and `.xaml.cs:354` | Speed test Start/Cancel merged into one toggle. |
| `Pages/SnipPage.xaml:838` | Status-bar link is now a stock `HyperlinkButton`. Padding is reset so it sits on the status line. |
| `Pages/SnipPage.xaml:850` | Toast tap target is now a stock `Button`, for keyboard, focus, and pointer handling. |
| `Pages/SnipPage.xaml.cs:1142` | Favourite star hover-reveal is kept. It is a visibility state, not an animation. |
| `Pages/ThreadTunerPage.xaml:263` | Row chevron resets MinWidth/MinHeight/Padding so it fits the fixed 12x16 indent slot. The stock 32px minimum height would break row alignment. |
| `Themes/KaliteTheme.xaml:345` | `DefaultContentDialogStyle` keeps its FontFamily. That is typography, which is out of scope. The stock Background was removed. |
| `ViewModels/BenchmarkViewModel.cs:635` | Merged Start capture / Stop pair. |
| `GpuOverclock/Views/OverclockSection.xaml` (Keep button) | Keep is stock, not accent. The risk gate can show at the same time, because the VM does not gate apply on `RiskAccepted`, and a view may have one accent button. The risk gate keeps its accent. |

### Not tagged in code, logged here

- **`ContentDialog.Resources` size tokens** (`ThreadListDialog`, `NvidiaComponentPickerDialog`, `RuleEditorDialog`, `AffinityPage` DeviceDetailsDialog, `InstallerPage` BrowserDetailsDialog): these set `ContentDialogMinWidth`/`MaxWidth`/`MaxHeight` only. They are layout sizing, not button styling, so they stay. Each is still a stock `ContentDialog`.

## Checks

Run:
- Build: `dotnet build src/kaliteConfig/kaliteConfig.csproj`, exit 0, 0 warnings, 0 errors.
- Grep for removed style names: no matches.
- Grep for TODO/FIXME/XXX on the branch diff: none.
- Grep for `BUTTON-REVIEW`: 12 source comments. Each has a resolution in the table above.
- Accent count per XAML file: every file has one `AccentButtonStyle` use per view, after the Keep fix. Files with more than one use are checked per view.

Not run (limitations, not passes):
Gallery references (WinUI Gallery, `microsoft/WinUI-Gallery` `main`; the repo pins `WindowsAppSdkPackageVersion` 2.5.4-experimental in `standalone.props`, not a release tag):
- **Button and accent style:** `WinUIGallery/Samples/Button/ButtonPage.xaml` and `ButtonBuiltInStyles.txt` show `AccentButtonStyle` and `SubtleButtonStyle`. The gallery does not state the one-accent-per-view rule. That rule comes from this task.
- **ToggleButton, one label that follows state (benchmark capture, speed test):** no precedent. `WinUIGallery/Samples/ToggleButton/ToggleButtonPage.xaml` uses a fixed label and a separate On/Off text. The label-follows-state pattern is this project's choice.
- **ContentDialog defaults:** `WinUIGallery/Samples/ContentDialog/ContentDialogPage.xaml` sets `DefaultButton` to `Primary`. The `PrimaryButtonStyle` use in dialogs was not confirmed.
- **HyperlinkButton (snip status-bar link):** `WinUIGallery/Samples/HyperlinkButton/HyperlinkButtonPage.xaml`.
- **Accent and Keep decisions (OverclockSection):** not covered by the gallery. Based on the one-accent rule.

Not run (limitations, not passes):
- **Light, dark, and high contrast rendering:** not verified. The app could not be launched and screenshotted here. This needs a manual check.
- **Feature walkthrough:** the merged toggles and the converted lookalikes were checked by reading code and building. They were not run through the UI.

## Manual checklist

1. Light, dark, and high contrast: each page renders buttons with stock states (rest, hover, pressed, disabled, focus).
2. Benchmark: Start capture changes to Stop and back, and the glyph follows.
3. Speed test: Start changes to Cancel while running, and cancelling returns to Start.
4. BIOS value pill and thread tuner chevron: click opens the same action as before.
5. Snip status-bar link navigates to Settings, and the toast dismisses on click.
6. Nav rail and title bar look the same as before.
7. Each dialog opens, its Primary/Close buttons respond, and the default button is correct.
