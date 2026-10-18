# INVENTORY — stock WinUI 3 remaster

Branch: `remaster/stock-winui`
Baseline commit: `8f90d17` "Baseline before stock WinUI 3 remaster"

## Project facts (detected)

| Fact | Value |
|---|---|
| Solution/project root | `E:\Users\Administrator\source\repos\kaliteconfig`, projects under `src/` |
| Windows App SDK | `Microsoft.WindowsAppSDK` **2.4.0** |
| TargetFramework | `net10.0-windows10.0.22621.0` |
| TargetPlatformMinVersion | `10.0.17763.0` |
| Packaged / unpackaged | **Unpackaged** (`<Packaged>false</Packaged>`, `WindowsPackageType=None`, `EnableMsixTooling=false`) |
| Side nav | **KEEP** (default) — 68px icon rail, custom `NavigationViewItem` template |
| Window backdrop / title bar | **LEAVE** (default) — `KaliteBackdrop` control, custom |
| CommunityToolkit.SettingsControls | **Yes**, `8.2.251219` — available for settings rows |
| Localization | **None** — 0 `.resw` files, 0 `x:Uid` usages. Strings are inline literals. |
| Can build | **Yes** |
| Can run / screenshot | **No** — no interactive display in this environment |
| Test project | **No** |
| Baseline build | **0 errors, 0 warnings** |
| WinUI Gallery reachable | **Yes** (`github.com/microsoft/WinUI-Gallery`, default branch reachable) |

### Extra packages already referenced (may keep)
- `DevWinUI` 10.4.1 — third-party theme dictionary, merged in `App.xaml`
- `Microsoft.Graphics.Win2D` 1.4.0 — chart/canvas rendering for Overclock + Snip preview
- `CommunityToolkit.Mvvm` 8.4.2
- `LLT.NvAPIWrapper.Net`, `System.Management`, `System.Drawing.Common`, `System.ServiceProcess.ServiceController`

### Scale
| Metric | Count |
|---|---|
| XAML files (source, excluding obj/bin) | **39** |
| XAML lines | **8,639** |
| Custom `Kalite*` style references in XAML | **432** across **27** files |
| Hardcoded hex colours in XAML | **125** |
| Hardcoded colour constructions in C# | **~90** across **17** files |
| `CornerRadius="<number>"` in XAML | **185** |
| `FontSize` / `FontFamily` / `FontWeight` attributes in XAML | **~500** |
| Custom `ControlTemplate`s | **1** (`MainWindow.xaml`, the nav rail) |
| `ContentDialog` hosts | **18** code-behind files |
| Secondary windows | **4** (`SnipOverlayWindow`, `SnipPinWindow`, `SnipTrayWindow`, `SnipViewerWindow`) |
| Converters | **7** |

## Global layer

### `App.xaml` (23 lines)
Merged dictionaries, in order:
1. `XamlControlsResources`
2. `DevWinUI/Themes/Generic.xaml` (third-party theme)
3. `Themes/KaliteTheme.xaml` (custom)

Plus one **implicit style** that overrides stock `TextBlock`:
`FontFamily` → `KaliteBodyFontFamily`, `FontSize` → `14`, `LineHeight` → `20`.

**Verdict:** implicit style must go (stock type ramp wins).

### `Themes/KaliteTheme.xaml` (286 lines) — the main offender
**A. Re-declares stock WinUI theme resources under the same keys**, shadowing the stock values app-wide:
`TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`, `TextFillColorTertiaryBrush`,
`TextFillColorDisabledBrush`, `TextFillColorPrimaryDisabledBrush`,
`DividerStrokeColorDefaultBrush`, `ControlStrokeColorDefaultBrush`, `ControlStrokeColorStrongBrush`,
`CardStrokeColorDefaultBrush`, `CardStrokeColorDefaultSolidBrush`,
`CardBackgroundFillColorDefaultBrush`, `CardBackgroundFillColorSecondaryBrush`, `CardBackgroundFillColorTertiaryBrush`,
`LayerFillColorDefaultBrush`, `LayerFillColorAltBrush`, `LayerFillColorDefaultSolidBrush`,
`SolidBackgroundFillColorBase`, `SolidBackgroundFillColorBaseAlt`,
`SystemFillColorSuccessBrush`, `SystemFillColorCautionBrush`, `SystemFillColorCriticalBrush`,
`ContentDialogBackground{,Dark1,Dark2,Light1,Light2}`, `ContentDialogBorderBrush`,
`ContentDialogSeparatorBackground`, `AppForegroundColor`,
`ApplicationPageBackgroundThemeBrush` (forced **Transparent**),
`ControlCornerRadius` (4), `OverlayCornerRadius` (5), `ListViewItemCornerRadius` (3), `GridViewItemCornerRadius` (3).

Also declares a hardcoded system accent colour (documented separately).

**B. Shadows the stock type-ramp styles** by redefining the same keys:
`CaptionTextBlockStyle`, `BodyTextBlockStyle`, `BodyStrongTextBlockStyle`,
`TitleTextBlockStyle`, `SubtitleTextBlockStyle`, `HeaderTextBlockStyle`, `DefaultContentDialogStyle` —
each re-`BasedOn` a snapshot copy and then overrides `FontFamily`/`FontSize`.

**C. Custom fonts** — 3 `FontFamily` resources plus overrides of
`XamlAutoFontFamily` and `ContentControlThemeFontFamily` (the latter is a stock WinUI key).

**D. Custom styles (all non-stock)** — 4 snapshot copies (`KaliteSnap.*`) + 10 custom:
`KaliteDisplayText`, `KalitePageTitle`, `KaliteSectionTitle`, `KaliteEyebrow`, `KaliteCard`,
`KaliteEntranceGrid`, `KaliteEntranceScrollViewer`, `KaliteEntranceBorder`, and an
**implicit `ItemsRepeater` style**.

**E. Non-stock animation** — `EntranceThemeTransition` applied by `KaliteCard`, the three
`KaliteEntrance*` styles, and the implicit `ItemsRepeater` style. Plus
`NavShell` transitions in `MainWindow.xaml`.

**F. Custom corner radii** — `KaliteRailPlateRadius` 3, `KaliteCardRadius` 8, `KaliteChipRadius` 2.

## Nav guard inputs (nav is KEPT — must be preserved exactly)
`MainWindow.xaml` (443 lines) owns the rail:
- `NavigationView` with `OpenPaneLength`/`CompactPaneLength` = 68, `PaneDisplayMode="Left"`, `IsPaneOpen="True"`, no back button, no settings item, no pane toggle.
- A `NavRailMask` `Border` (Width 68) painted behind the rail.
- `NavigationView.Resources` holds: 6 `NavigationView*` background brush overrides,
  the `RailNavItemStyle` with a **custom `ControlTemplate`** for `NavigationViewItem`
  (6 custom `VisualState`s: Normal/PointerOver/Pressed/Selected/PointerOverSelected/PressedSelected),
  custom `ItemBody`/`SelectionPlate` borders, a `Viewbox`-wrapped icon with a `ScaleTransform`,
  and a fixed 11px SemiBold label.
- 11 icon-rail items, each `AutomationProperties.Name`-tagged, most with a `ToolTip` collapsed.
- Sits on `KaliteBackdrop` + `BgImageLayer` + `TintLayer`; nav is transparent over the backdrop.

**Exceptions to carry into `Themes/SideNavResources.xaml`:** the 6 `NavigationView*` brush
overrides, `RailNavItemStyle` and its `ControlTemplate`, the 6 `VisualState`s, the fixed
60×56 item geometry, the 11px label, `KaliteRailPlateRadius`, and the collapsed tooltips.
Backdrop must not change.

## Per-page inventory and planned stock layout

| File | Lines | Current structure | Planned stock layout |
|---|---|---|---|
| `Pages/SnipPage.xaml` | 826 | Mode tiles, gallery, strip, settings, 15 `Kalite*` refs | Page header + `SelectorBar` modes + `GridView` gallery + card sections; stock `GridViewItem` containers |
| `Pages/UninstallerPage.xaml` | 721 | 3-pane-ish list + side panel, 99 font attrs | `Grid` 2-pane, source `ListView` + detail card, `Expander` sections |
| `GpuOverclock/Views/OverclockSection.xaml` | 679 | Custom-drawn canvas charts, sliders+textbox | `Slider` + `NumberBox`, `Expander` groups; canvas charts kept, colours from theme brushes |
| `Pages/NetworkPage.xaml` | 500 | Adapter list + system-wide rows | `ListView` adapters + `Expander` system rows |
| `Pages/BenchmarkPage.xaml` | 496 | Capture controls + compare list | Card header + `Expander` groups + `ListView` runs |
| `Pages/AffinityPage.xaml` | 492 | Rules list + editor | `ListView` rules + `Expander` rule editor |
| `MainWindow.xaml` | 443 | **Nav (KEEP)** | Unchanged except resource move |
| `Pages/ThreadTunerPage.xaml` | 368 | Process/thread tables | `ListView` + `Grid` item template + header row |
| `Pages/BiosManagerPage.xaml` | 349 | Export/apply flow | Card sections + `ProgressBar` + checklist |
| `Pages/InstallerPage.xaml` | 299 | Product list + progress | `ListView` + `InfoBar` + `ProgressBar` |
| `Themes/KaliteTheme.xaml` | 286 | Custom theme layer | Delete / reduce to stock |
| `Pages/SettingsPage.xaml` | 266 | Sectioned settings | SettingsControls `SettingsCard` |
| `Controls/RuleEditorDialog.xaml` | 264 | Dialog form | Stock `ContentDialog` |
| `Pages/PowerPlansPage.xaml` | 262 | Plan list | `ListView` + cards |
| `Pages/GpuDriversPage.xaml` | 258 | Driver cards | `ListView` cards + `InfoBar` |
| `Windows/SnipOverlayWindow.xaml` | 231 | Custom-drawn overlay chrome | Keep custom drawing (tool), restock chrome |
| `Pages/PriorityBoostsPage.xaml` | 225 | Boost list | `ListView` + `Expander` |
| `Controls/ThreadListDialog.xaml` | 141 | Dialog table | Stock `ContentDialog` + `ListView` |
| `Pages/PersonalizePage.xaml` | 140 | `SelectorBar` + sections | Already close to stock; restock |
| `PackageManager/Views/*` (8 files) | ~640 total | Package lists | `ListView` + selection + bulk `CommandBar` |
| `GpuOverclock/Views/AmdGpuSection.xaml` | 102 | ADLX rows | `Expander` + stock rows |
| `Controls/NvidiaComponentPickerDialog.xaml` | 88 | Dialog | Stock `ContentDialog` |
| `Windows/SnipViewerWindow.xaml` | 84 | Viewer shell | Keep custom preview |
| `Pages/ReservedCpuSetsPage.xaml` | 70 | CPU set list | `ListView` |
| `Controls/SnipPreviewView.xaml` | 71 | Custom Win2D preview | Keep custom (drawing surface) |
| `Windows/SnipTrayWindow.xaml` | 66 | Tray window | Restock |
| `Controls/KaliteBackdrop.xaml` | 63 | Backdrop (LEAVE) | Unchanged |
| `Pages/WindowsSettingsHubPage.xaml` | 57 | Hub | Restock |
| `Pages/OverclockPage.xaml` | 38 | Host page | Restock |
| `Pages/GraphicsHubPage.xaml` | 36 | Hub | Restock |
| `Windows/SnipPinWindow.xaml` | 20 | Pin window | Restock |
| `PackageManager/Views/PackageListView.xaml` | 21 | Shared list | Restock |

## Converters
`AssetPathToImageSourceConverter`, `BoolToGridLengthConverter`, `BoolToVisibilityConverter`,
`ImageFileThumbnailConverter`, `InvertedBoolConverter`, `InvertedBoolToVisibilityConverter`,
`NullToVisibilityConverter`. All are value transforms, not visual chrome — all stay.

## Resource keys read from C# (must not be deleted blindly)
`AccentFillColorDefaultBrush`, `BodyTextBlockStyle`, `CaptionTextBlockStyle`,
`ChangeSummaryTemplate`, `DefaultContentDialogStyle`, `DividerStrokeColorDefaultBrush`,
`TextFillColorSecondaryBrush`, `TextFillColorTertiaryBrush`, `MeteorUp`, `MeteorDown`.

`MeteorUp` / `MeteorDown` appear to be animation resource names — **review item**.

## Custom-drawn / third-party visuals
- `GpuOverclock/Views/OverclockSection.xaml.cs` — canvas graph drawing (Win2D), 9 hardcoded colours
- `Pages/BenchmarkPage.xaml.cs` — 20 hardcoded colours (chart series)
- `Windows/SnipOverlayWindow.xaml.cs` — canvas annotation/selection drawing, 19 hardcoded colours
- `Controls/SnipPreviewView.xaml.cs` — Win2D progressive image + custom zoom/pan
- `Windows/SnipTrayWindow.xaml.cs` — encoder wait path
- `Controls/KaliteBackdrop.xaml.cs` — backdrop (LEAVE unchanged)

These keep their libraries; their colours must move to theme-brush lookups with logic unchanged.

## Explicit `RequestedTheme`
Theme-setting logic in `ThemeSettingsService` / `ThemeHelper` is user-facing and stays.
Any other `RequestedTheme` is a violation to remove.

## Review items carried forward
1. `MeteorUp` / `MeteorDown` — confirm whether still used anywhere; delete if dead.
2. `DevWinUI` third-party theme — decide whether it stays merged or is dropped. Conservative: keep the merge, remove only the implicit `TextBlock` override.
3. `ApplicationPageBackgroundThemeBrush` forced to `Transparent` — needed for the transparent-backdrop design; nav-adjacent, so it moves to the nav dictionary rather than being deleted outright.
4. Uninstaller page has 99 font attributes — largest single-page typography migration.
