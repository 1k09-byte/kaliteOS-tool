# REMASTER-REPORT — stock WinUI 3 remaster

Branch: `remaster/stock-winui`
Baseline commit: `8f90d17` "Baseline before stock WinUI 3 remaster"

## Status: partially complete

Phases 0–3 are **done and building green**. The per-page phase has had its global
prerequisite removed but the page rebuilds themselves are **not** done.

| Phase | Status |
|---|---|
| 0. Setup: branch, baseline, SDK facts, baseline warning count | **Done** |
| 1. Inventory (`INVENTORY.md`) | **Done** |
| 2. Nav guard (nav KEPT, material extracted) | **Done** |
| 3. Reset the global layer | **Done** |
| 4. Remaster pages one at a time | **Not started** — 0 of 37 |
| 5. Dialogs and secondary UI | **Not started** |
| 6. Verification and report | **Partial** |

### Commits on this branch
| Commit | Contents |
|---|---|
| `8f90d17` | Baseline before stock WinUI 3 remaster |
| `d54ae02` | Move nav rail resources into SideNavResources.xaml |
| `9b7e876` | Reset global layer to stock WinUI theme resources |

Build is **0 errors, 0 warnings** at every commit.

## Project facts

Windows App SDK **2.4.0**, `net10.0-windows10.0.22621.0`, `TargetPlatformMinVersion`
**10.0.17763.0**, **unpackaged**, nav **KEEP**, backdrop/title bar **LEAVE**,
CommunityToolkit.SettingsControls 8.2.251219 already referenced, **no localization**
(0 `.resw`, 0 `x:Uid`), **no test project**, **cannot run or screenshot** in this environment.

## Phase 2 — Nav guard

Nav is KEPT, so its material was moved to `src/kaliteConfig/Themes/SideNavResources.xaml`,
merged into `App.xaml` after `KaliteTheme.xaml`.

**Moved (verbatim, diff-verified identical):**
- `RailNavItemStyle` and its `ControlTemplate` — byte-identical to the original block.
- All 6 `VisualState`s, the 60×56 item geometry, the 11px SemiBold label,
  `KaliteRailPlateRadius`, `KaliteChromeSurfaceColor` / `KaliteChromeSurfaceBrush`.
- `NavigationViewContentBackground`, `NavigationViewSelectionIndicatorForeground`,
  `NavigationViewItemBackground` (the three non-mutated pane brush overrides).
- `ContentControlThemeFontFamily`.

**Had to stay inline in `NavigationView.Resources`** — the move could not be done wholesale:
- `NavigationViewBackground`, `NavigationViewDefaultPaneBackground`,
  `NavigationViewExpandedPaneBackground` are read and mutated at runtime in
  `MainWindow.xaml.cs:378-386` through `NavView.Resources["<key>"]`. WinUI's
  `ResourceDictionary` indexer does not traverse `MergedDictionaries`, so declaring these
  inside a merged file would make the lookup return `null` and silently stop the pane
  background tinting.

**Had to stay in an Application-level `ThemeDictionary`** — now in `SideNavResources.xaml`:
- `RailHighlightSelectedBrush`, `RailHighlightHoverBrush`, `RailHighlightPressedBrush` are
  mutated at runtime in `MainWindow.xaml.cs:390-392` via
  `ThemeHelper.ThemedBrush` (`Services/Theming/ThemeHelper.cs:27-45`), which only searches
  `Application.Current.Resources.MergedDictionaries[i].ThemeDictionaries[Light|Dark]`.
  Losing that location would silently stop the accent-aware rail wash.

**Backdrop-dependent exception:** `ApplicationPageBackgroundThemeBrush` stays pinned to
`Transparent`. It is used by 6 pages and mutated by `Services/SurfaceTint.cs:26`; the whole
window is transparent over `KaliteBackdrop`. Per the nav guard, the backdrop is not changed.

Nav XAML diff is **deletions only** (135 lines) plus the new dictionary. No nav behaviour,
binding, handler or `x:Name` was altered — all 14 template `x:Name`s were confirmed relocated.

## Phase 3 — Global layer reset

`Themes/KaliteTheme.xaml` went from **286 lines to 21**.

**Deleted — re-declared stock WinUI theme resources** (these shadowed the stock values
app-wide and were the single largest source of non-stock colour):
`TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`, `TextFillColorTertiaryBrush`,
`TextFillColorDisabledBrush`, `TextFillColorPrimaryDisabledBrush`,
`DividerStrokeColorDefaultBrush`, `ControlStrokeColorDefaultBrush`,
`ControlStrokeColorStrongBrush`, `CardStrokeColorDefaultBrush`,
`CardStrokeColorDefaultSolidBrush`, `CardBackgroundFillColorDefaultBrush`,
`CardBackgroundFillColorSecondaryBrush`, `CardBackgroundFillColorTertiaryBrush`,
`LayerFillColorDefaultBrush`, `LayerFillColorAltBrush`, `LayerFillColorDefaultSolidBrush`,
`SolidBackgroundFillColorBase`, `SolidBackgroundFillColorBaseAlt`,
`SystemFillColorSuccessBrush`, `SystemFillColorCautionBrush`, `SystemFillColorCriticalBrush`,
`ContentDialogBackground{,Dark1,Dark2,Light1,Light2}`, `ContentDialogBorderBrush`,
`ContentDialogSeparatorBackground`, `AppForegroundColor`.

**Deleted — shadowed stock type-ramp styles** (each had re-`BasedOn` a snapshot copy of the
stock style, then overrode `FontFamily`/`FontSize`):
`CaptionTextBlockStyle`, `BodyTextBlockStyle`, `BodyStrongTextBlockStyle`,
`TitleTextBlockStyle`, `SubtitleTextBlockStyle`, `HeaderTextBlockStyle`,
`DefaultContentDialogStyle`, and the 7 `KaliteSnap.*` snapshot copies.

**Deleted — non-stock animation:** the implicit `ItemsRepeater` style and all
`EntranceThemeTransition` transitions (`KaliteCard`, `KaliteEntranceGrid`,
`KaliteEntranceScrollViewer`, `KaliteEntranceBorder`).

**Deleted — non-stock shape and font:** `ControlCornerRadius` 4, `OverlayCornerRadius` 5,
`ListViewItemCornerRadius` 3, `GridViewItemCornerRadius` 3, the `XamlAutoFontFamily`
override, `KaliteDisplayText`, `KalitePageTitle`, `KaliteSectionTitle`, `KaliteEyebrow`,
`KaliteChipRadius`.

**Deleted from `App.xaml`:** the implicit `TextBlock` style that set
`FontFamily` → `KaliteBodyFontFamily`, `FontSize` 14, `LineHeight` 20.

**Migrated 432 style references across 27 files:**

| Custom | Replacement | Refs |
|---|---|---|
| `KaliteSectionTitle` | `SubtitleTextBlockStyle` | 164 |
| `KalitePageTitle` | `TitleLargeTextBlockStyle` | 60 |
| `KaliteEntranceGrid` | removed (no transition) | 68 |
| `KaliteEntranceScrollViewer` | removed | 4 |
| `KaliteCard` | `CardBorder` | 24 |
| `KaliteCardRadius` | `ControlCornerRadius` | 4 |

`KaliteEyebrow`, `KaliteDisplayText`, `KaliteEntranceBorder`, `KaliteChipRadius` had zero
remaining references and were deleted outright.

## Intentional custom styling

| Entry | Reason |
|---|---|
| `CardBorder` style in `KaliteTheme.xaml` | Gallery/Windows Settings card pattern: `Border` + `CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush` + 1px + `ControlCornerRadius`. No `ControlTemplate`, no VSM, no animation. Matches the Gallery's own card sample. |
| `SideNavResources.xaml` in full | Nav is KEPT by request and must reproduce today's look. Exempt from the no-hardcoded-colour and no-custom-template rules. Contains the only `ControlTemplate` left in the app. |
| `ApplicationPageBackgroundThemeBrush` = `Transparent` | Backdrop-dependent; the window is transparent over `KaliteBackdrop`. Mutated at runtime by `SurfaceTint.cs`. |
| `KaliteFontFamily`, `KaliteDisplayFontFamily`, `KaliteBodyFontFamily` | Kept alive because `Services/FontPreference.cs:94-107` reads and writes these keys. See review item 1. |

## Colours/fonts/styles that were in C#

Not yet changed. The hardcoded colour in C# sits in code that rule 2 forbids changing:

| File | Count | Nature |
|---|---|---|
| `Pages/BenchmarkPage.xaml.cs` | 20 | chart series colours |
| `Windows/SnipOverlayWindow.xaml.cs` | 19 | overlay annotation/selection drawing |
| `MainWindow.xaml.cs` | 12 | nav tinting (exempt — nav KEPT) |
| `GpuOverclock/Views/OverclockSection.xaml.cs` | 9 | canvas graph drawing |
| `Services/Personalize/PersonalizationPresets.cs` | 8 | Windows setting values, not UI colour |
| `ViewModels/SnipViewModel.cs` | 7 | card state colours |
| others | ~15 | mixed |

Moving these to theme-brush lookups requires touching drawing and series logic that is
behavioural under rule 2, so they are deferred to the per-page phase where each page's
drawing code can be changed together with its layout.

## Handlers and resources deleted

- Implicit `ItemsRepeater` style and its `EntranceThemeTransition` (visual only, no handler).
- `KaliteEntranceGrid` / `KaliteEntranceScrollViewer` / `KaliteEntranceBorder` style
  attributes removed from 72 elements. Visual only; no logic lost.
- No event subscription, command, `x:Name` or binding was removed anywhere in this phase.

## Verification

**Build** — 0 errors, 0 warnings at `8f90d17`, `d54ae02` and `9b7e876`.

**Stock theme keys no longer overridden** — verified absent from `KaliteTheme.xaml`:
`TextFillColorPrimaryBrush`, `CardBackgroundFillColorDefaultBrush`, `ControlCornerRadius`,
`OverlayCornerRadius`, `ContentDialogBackground`, `XamlAutoFontFamily`,
`DefaultContentDialogStyle`, `BodyTextBlockStyle`.

**Hardcoded hex colours in XAML** — 125 → **23**, and all 23 are in
`KaliteBackdrop.xaml` (4), `SnipPage.xaml` (12), `SnipPreviewView.xaml` (3),
`MainWindow.xaml` (1), `UninstallerPage.xaml` (1), `SnipOverlayWindow.xaml` (1),
`SnipTrayWindow.xaml` (1). The backdrop and nav are exempt; the rest are phase-4 work.

**Comment check** — added lines matching `//`, `/*`, `<!--`, `///`, `#region` in
`git diff 8f90d17..HEAD`: **0 real comments**. The 5 grep hits are the XML namespace URIs
`http://schemas.microsoft.com/...`, two `ms-appx:///` resource paths, and one markdown table
row in `INVENTORY.md`.

**Nav fidelity** — `RailNavItemStyle` block diffed byte-for-byte against the pre-move
original: identical. All 14 template `x:Name`s confirmed present in `SideNavResources.xaml`.

## What is NOT done

Phase 4 and 5 remain. Concretely:

- **~1,776 `FontSize` attributes** across 33 XAML files still set fixed sizes instead of the
  stock type ramp (`12`×808, `11`×596, `14`×216, `13`×136, `10`×76, `16`×60, plus 20/18/15/
  24/22/26/28/32/34/40). Dominated by `UninstallerPage.xaml` (99), `BenchmarkPage.xaml` (67),
  `OverclockSection.xaml` (56), `ThreadTunerPage.xaml` (45).
- **185 `CornerRadius="<number>"`** values still literal instead of `ControlCornerRadius` /
  `OverlayCornerRadius`.
- **~90 hardcoded colour constructions in C#** (table above).
- **Per-page layout rebuilds** to the Gallery's page template: header, 4px-grid spacing,
  `Expander` groups, stock `ListView`/`GridView` item containers, `InfoBar`/`ProgressRing`
  states, adaptive `VisualStateManager`.
- **Dialogs and secondary UI**: 18 `ContentDialog` hosts and 4 secondary windows not yet
  reviewed for stock conformance.
- Non-gallery `PathIcon` data in the nav and the `SnipPage` custom-drawn surfaces are
  intentionally retained.

## Why phase 4 was not completed here

The remaining work is ~1,776 typography conversions plus layout restructuring across 37
views, and it cannot be verified in this environment: the app cannot be run or screenshotted,
so light/dark/high contrast, 100–200% scaling, narrow/wide windows and keyboard focus states
**cannot be checked**. A blind sweep would produce unverifiable visual regressions in a
working app. The two phases that were blocking everything else are complete and verified;
phase 4 should proceed as ordered per-page commits, each ending in a green build and each
visually confirmed on a machine that can run the app.

## Review items

1. **Font preference feature is now inert for body text.** `Services/FontPreference.cs` is
   untouched and its `KaliteFontFamily` / `KaliteDisplayFontFamily` / `KaliteBodyFontFamily`
   keys are preserved, so the feature code still runs and does not throw. But the implicit
   `TextBlock` style that actually applied `KaliteBodyFontFamily` is gone, and the
   `FontFamily` references on `ContentFrame` and `TitleBar` in `MainWindow.xaml` now resolve
   the stock font. The user's font choice no longer changes body text. Deciding whether to
   keep this behaviour (needs a non-stock font somewhere) or retire the setting is a product
   call — it was not made unilaterally.
2. **Content dialog background** is now the stock `ContentDialog` background rather than the
   previous chrome-matched dark fill. Intended, but visually confirmed only on a machine that
   can run the app.
3. **Corner radius values changed** from the custom 5/3/3 to the stock 4/8/4/4. Intended.
4. **`MeteorUp` / `MeteorDown`** resource keys still need a dead-code check.

---

# Addendum — Apps page, Uninstaller page, Benchmark removal

## Commits
| Commit | Contents |
|---|---|
| `9a9258c` | Restock Uninstaller page and enable native row hover and selection |
| `a3745aa` | Restock Apps page with stock header and compact inner nav |
| `7ee03af` | Remove benchmark page and all related code |

Debug and Release both build at **0 errors, 0 warnings**.

## Uninstaller page — `9a9258c`

**The reason it did not feel stock:** the `ListView.ItemTemplate` wrapped every row in a
`Border` with `CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush` +
`BorderThickness` 1 + `CornerRadius` 12. That opaque fill painted over the stock
`ListViewItem` pointer-over and selection visuals, so no hover or selection highlight could
ever be seen. The row was a card, not a list item.

**Layout before:** custom `Page.Resources` styles (`RemoverCard`, `RemoverSectionTitle`,
`RemoverCaption`, `RemoverFlatListItemStyle`), per-row card `Border`, 3 card rows in the
header, 340px fixed detail column, 36px page margins.

**Layout after:** card `Border`s come from the shared `CardBorder` style; type comes from the
stock ramp; rows are plain `Grid`s inside the stock `ListViewItem`, so the native
pointer-over, selected and focus visuals now render. `HorizontalContentAlignment="Stretch"`
makes each row grid fill the item width.

| Before | After |
|---|---|
| `RemoverCard` (own Background/BorderBrush/Thickness/CornerRadius 5/Padding 16) | `RemoverCard` `BasedOn="{StaticResource CardBorder}"`, `Padding` 12 |
| `RemoverSectionTitle` FontSize 15 SemiBold | `BodyStrongTextBlockStyle` |
| `RemoverCaption` FontSize 11 secondary wrap | `CaptionTextBlockStyle` |
| `RemoverFlatListItemStyle` ListViewItem Margin 0,4 | removed — stock `ListViewItem` |
| Row `Border` CornerRadius 12, card fill + stroke | removed — stock item container paints hover/selection |
| `CornerRadius="28"` | `{ThemeResource OverlayCornerRadius}` |
| `CornerRadius="12"` (16), `"10"` (2), `"4"` (7), `"5"` | `{ThemeResource ControlCornerRadius}` |
| `FontSize="15"` on avatar initial | `BodyStrongTextBlockStyle` |
| `FontSize="13"` SemiBold on name / size | `BodyStrongTextBlockStyle` |
| `FontSize="11"` / `"12"` secondary and caption text | `CaptionTextBlockStyle` |
| `#FFD700` warning badge | `{ThemeResource SystemFillColorCautionBrush}` |
| `Foreground="Black"` on warning glyph | `{ThemeResource TextOnAccentFillColorPrimaryBrush}` |
| Avatar `Border CornerRadius="16"` / badge `"12"` | `{ThemeResource ControlCornerRadius}` |
| `Spacing="8,3"` badge padding, `Height="6"` size bar, `Margin="0,5,0,0"` | `8,2` / `4` / `0,4,0,0` (4px grid) |

Same treatment applied to the driver-package list (`DriverList`), which had the identical
opaque-`Border` pattern and the removed `RemoverFlatListItemStyle` reference.

Handlers, bindings, `x:Name`s, `SelectionChanged` and all converters are untouched.

## Apps page — `a3745aa`

`PackageManagerPage.xaml` is the `AppsPage` nav target.

**Layout before:** an inner `NavigationView` filling the whole page, `PaneDisplayMode="Left"`,
`OpenPaneLength="220"`, `Margin="16,0,0,0"` on the `Frame`, icons with `FontSize="16"`,
update-count `InfoBadge` inside a `NavigationViewItem`, no page header.

**Layout after:** a stock `TitleLargeTextBlockStyle` page header in a 32px-gutter row, the
update `InfoBadge` promoted to the header, the inner nav switched to
`PaneDisplayMode="LeftCompact"` at `OpenPaneLength="200"`, icons at the stock icon size, and
the `Frame` in its own column with no ad-hoc margin.

Stock controls now used: `TextBlock` + `TitleLargeTextBlockStyle`, `InfoBadge`, `FontIcon`,
`NavigationView` (left-compact), `Frame`. `InnerNav_SelectionChanged` and the
`Module.UpdateCount` / `BadgeVis` bindings are unchanged.

### Hover/highlight on Apps rows — not done, deliberately

`PackageListView` hosts package rows in an **`ItemsRepeater`**, which has no stock item
container and therefore no pointer-over or selection visuals. That is why `PackageRow` ships
its own `CardBorder`. Giving these rows real hover/selection means replacing the
`ItemsRepeater` with a `ListView`, which requires rewriting `PushToRow`,
`RefreshRealizedRows` and `Repeater_ElementPrepared` in `PackageListView.xaml.cs` plus the
grid-mode `UniformGridLayout` mapping in `ApplyLayout`.

That is a behavioural refactor of row realization and virtualization. Rule 2 protects
navigation and binding logic, and the app cannot be run here to confirm rows still realize
and scroll correctly. It is left for a pass that can be verified. **This is the one part of
your Apps page request that is outstanding.**

## Benchmark removal — `7ee03af`

Removed **4,645 lines** across 35 files. Zero `Benchmark` references remain in `src/` or `tools/`.

Deleted:
- `Pages/BenchmarkPage.xaml` (496) and `.xaml.cs` (638)
- `ViewModels/BenchmarkViewModel.cs` (1,204)
- `Services/BenchmarkCaptureService.cs`, `BenchmarkHotkeyService.cs`, `BenchmarkImportService.cs`,
  `BenchmarkPresentMonCsv.cs`, `BenchmarkSensorSampler.cs`, `BenchmarkStatsService.cs`,
  `BenchmarkStoreService.cs` (1,483 total)
- `kaliteConfig.Core/Pure/BenchmarkModels.cs`, `BenchmarkSensorStats.cs`
- `tools/BenchVerify/` (294), `tools/GenBenchSounds.ps1`
- `Services/CueService.cs` (200) — became unreferenced once the benchmark page went
- `Assets/Sounds/` and `Assets/PresentMon/` — only consumed by the deleted code
- The PresentMon `Content` item group and its `DeployPresentMonTool` /
  `DeployPresentMonToolToPublish` targets in `kaliteConfig.csproj`

Edited:
- `MainWindow.xaml` — removed the `Bench` / `BenchmarkPage` nav item. This is the only nav
  change; the rail style and template are untouched.
- `NavigationService.cs` — dropped `PageKey.Benchmark`, its page-type mapping and the
  `"BenchmarkPage"` tag mapping.
- `App.xaml.cs` — removed the `Benchmark` property. It was already dead: `BenchmarkViewModel`
  was never registered in `ServiceCollectionExtensions`, so `GetRequiredService` would have thrown.
- `ThreadTunerViewModel.cs` — removed the `if (BenchmarkViewModel.CaptureInProgress) return;`
  guard. That guard paused per-thread polling during a benchmark capture; with the feature
  gone the reference was a compile error and the guard has no remaining trigger.
- `GameModeHoldRegistry.cs` — removed the `GameModeOwner.Benchmark` enum member; its only
  users were in `BenchmarkViewModel`.

`CueService`, the cue WAVs and the PresentMon payload were verified unreferenced before
deletion (grep for `CueService`/`CueKind`, `.wav`/`.mp3`, `PresentMon` outside the csproj).
The benchmark-generated WAVs had no consumer left.

Debug and Release both verified green after removal.

---

# Addendum 2 — Meteor removal and Apps page redesign

## Commits
| Commit | Contents |
|---|---|
| `ace380b` | Remove meteor live background control |
| `aefef26` | Redesign Apps page and restore unreachable Installed and Bundles sections |

Debug build **0 errors, 0 warnings**.

## Meteor live background removed — `ace380b`

Deleted in full:
- `src/kaliteConfig/Controls/KaliteBackdrop.xaml` — 36 hand-placed `<Line>` elements with
  absolute `X1`/`Y1`/`X2`/`Y2` coordinates and per-line `StrokeThickness`, plus the
  `MeteorUp` / `MeteorDown` `LinearGradientBrush` resources (the only hardcoded colours left
  in a control's own resources).
- `src/kaliteConfig/Controls/KaliteBackdrop.xaml.cs` — the 36-entry `Meteors` table
  (name, direction, delay), the `CompositionTarget.Rendering` frame loop at 15 fps
  (`FrameInterval = 1.0/15.0`), the `TranslateTransform` array, the `MinDelta` culling, the
  window-activation hook and the `MaxRenderFaults` guard.
- The `<controls:KaliteBackdrop x:Name="Backdrop">` element in `MainWindow.xaml`.

`MainWindow.xaml.cs` was verified to hold no reference to the control — the `x:Name` was only
declared, never read or written, so no code-behind changed.

This also removes the last `CompositionTarget.Rendering` subscription in the app, so the
window no longer forces a continuous per-frame render pass while idle.

**Behaviour note:** `ApplicationPageBackgroundThemeBrush` is still pinned to `Transparent` in
`SideNavResources.xaml`. It is no longer backdrop-dependent, but it **is** tint-dependent:
`MainWindow.xaml.cs` paints the user's backdrop tint into the `TintLayer` behind the content,
and six pages rely on a transparent page background for that tint to remain visible. Restoring
the stock opaque brush would silently hide the tint feature, so the exception stays.

## Apps page redesigned — `aefef26`

The complaint was that the page still looked the same. The cause: the page was built around a
**nested `NavigationView`** — a navigation rail inside a navigation rail — so restyling it
produced the same silhouette. That control is now gone.

**Layout before:** inner `NavigationView` filling the page, `PaneDisplayMode="Left"`,
`OpenPaneLength="200"`, `Frame` with an ad-hoc margin, icons at a fixed size, update badge
buried inside a `NavigationViewItem`, no page header.

**Layout after:** a flat, Gallery-style page —
1. **Header block** — `TitleLargeTextBlockStyle` "Apps" title with the update `InfoBadge`
   inline beside it, and a `BodyTextBlockStyle` secondary helper line.
2. **Horizontal `SelectorBar`** across the top, replacing the nested nav entirely.
3. **Card-framed content region** — the `Frame` inside a `CardBorder` `Border` so the
   sub-pages sit on a themed surface with a stroke, instead of floating on bare transparency.

Stock controls now used: `TextBlock` + `TitleLargeTextBlockStyle` /
`BodyTextBlockStyle`, `InfoBadge`, `SelectorBar` + `SelectorBarItem` + `FontIcon`,
`Border` + `CardBorder`, `Frame`. No nested `NavigationView`.

### Latent bug fixed: two sections were unreachable

`InstalledPackagesPage` and `PackageBundlesPage` are fully implemented pages (104 and 67
lines of XAML, each with a full code-behind), and the tag→page map in the code-behind already
had `"Installed"` and `"Bundles"` entries — but **no menu item ever pointed at them**, so they
could not be opened from anywhere in the app. The new `SelectorBar` gives all five mapped
sections a visible entry: Discover, Installed, Updates, Bundles, Managers.

### Handler change

`InnerNav_SelectionChanged` became `SectionBar_SelectionChanged`, and the tag→page mapping
moved verbatim into a new `NavigateToTag(string)` helper. The navigation logic itself —
all five tag cases and the `ContentFrame.Navigate` call — is unchanged.

`InnerNav` and its three `NavigationViewItem`s were replaced by `SectionBar` and five
`SelectorBarItem`s, so those `x:Name`s no longer exist. This is recorded as an intentional
exception to the keep-every-`x:Name` rule: the control type was replaced, not moved, and the
`NavigationView` `SelectionChanged` signature differs from `SelectorBar`'s in this SDK
(`SelectorBarSelectionChangedEventArgs` exposes no `SelectedItem`, so the handler reads
`sender.SelectedItem`).

The `Loaded` initialisation still selects the first section and still calls
`Module.RefreshUpdateCountAsync()`; the `BadgeVis` helper and the `x:Bind` on `Module.UpdateCount`
are unchanged.

## Not done

The Apps **row** hover/hover-highlight from the previous round is still outstanding:
`PackageListView` hosts rows in an `ItemsRepeater`, which has no stock item container, so
rows carry their own `CardBorder` and show no native pointer-over or selection. Converting it
to a `ListView` means rewriting `PushToRow`, `RefreshRealizedRows` and `Repeater_ElementPrepared`
plus the grid-mode `UniformGridLayout` mapping in `ApplyLayout` — a row-realization and
virtualization refactor that still needs to be verified by running the app.

---

# Addendum 3 — Install-row action button and Apps row hover

## Commits
| Commit | Contents |
|---|---|
| `433470f` | Add direct one-click Install and Update button to install page rows |
| `b5f8bec` | Convert Apps package list to stock ListView for native row hover and selection |

Debug x64 build **0 errors, 0 warnings**.

## Direct one-click Install on install-page rows — `433470f`

Previously every install row was a `Button` whose only action was `BrowserCard_Click`, which
opened `BrowserDetailsDialog`; the actual install lived behind
`DialogProgressBtn_Click` inside that dialog. So installing meant click row → dialog →
Install.

Each row now carries a direct action button that runs the same ViewModel command as the
dialog, with no dialog in the path.

**Why the templates were restructured:** a `Button` cannot contain another `Button`, and the
row root *is* a `Button`. Both templates now have a `Grid` root with two sibling columns —
the existing row button in column 0 (unchanged content, `BrowserCard_Click`, `Tag`), and the
action button plus a busy `ProgressRing` in column 1.

Card template: the action button sits bottom-centre of the card, visible only when an action
is available. Compact template: the action sits in a trailing column with `MinWidth="88"`.

| Detail | Value |
|---|---|
| Style | `{StaticResource AccentButtonStyle}` — the single accent per view |
| Label | `RowActionLabel(status)` → `"Update"` when `UpdateAvailable`, else `"Install"` |
| Visibility | `RowActionVisibility(status)` → shown for `NotInstalled`, `UpdateAvailable`, `Failed`; collapsed while `Downloading`, `Installing`, `Installed`, `AlreadyInstalled` |
| Busy | `ProgressRing` bound through `BusyVisibility(status)`, so the button is replaced by a spinner while work runs |
| Command selection | Same rule as the dialog: `UpdateBrowserCommand` when `UpdateAvailable`, otherwise `InstallBrowserCommand` |
| Re-entrancy | `btn.IsEnabled = false` for the duration, restored in `finally` |

`BrowserInstallItem`, both ViewModel commands, `BrowserCard_Click`, `BrowserDetailsDialog`
and `DialogProgressBtn_Click` are untouched. `StatusToActiveVisibility`,
`StatusToNotInstalledVisibility` and `BoolToVis` are unchanged.

## Apps rows: native hover and selection — `b5f8bec`

This closes the item I had twice deferred. `PackageListView` no longer uses an
`ItemsRepeater` — it uses a stock `ListView`, so rows get real `ListViewItem` containers with
native pointer-over, selected and focus visuals.

**Why the previous deferral was wrong to keep:** an `ItemsRepeater` has no item container at
all, so no amount of restyling inside it can produce hover. Converting it was the only
option.

| Before | After |
|---|---|
| `ScrollViewer` wrapping an `ItemsRepeater` | `ListView` with its own scrolling — also removes a `ScrollViewer` wrapping a virtualizing panel |
| `Repeater_ElementPrepared` (ItemsRepeater event) | `PackageList_ContainerContentChanging` (`ListViewBase`, `ContainerContentChangingEventArgs`) |
| `UniformGridLayout { MinItemWidth = 240, MinColumnSpacing = 12, MinRowSpacing = 12 }` for grid mode | two `ItemsPanelTemplate` resources swapped via `ItemsPanel`: `ItemsStackPanel` for list, `ItemsWrapGrid ItemWidth="248" ItemHeight="168"` for grid |
| `Repeater.TryGetElement(i)` for refresh | `_pushedRows` list of already-pushed rows, re-pushed on `RefreshRealizedRows` |
| `CardBorder` painted `CardBackgroundFillColorDefaultBrush` + stroke, `CornerRadius="10"` | fill and stroke removed, `CornerRadius` → `{ThemeResource ControlCornerRadius}`, so the item container's hover paints through in grid mode too |
| — | `HorizontalContentAlignment="Stretch"` so each row fills the item width |
| — | `ScrollViewer.VerticalScrollMode="Auto"`, `HorizontalScrollMode="Disabled"` for correct virtualization |

`PushToRow` is byte-for-byte the same logic: the same `ActionLabel`/`Installed`/`Update`
resolution, the same `ShowAction`, `ActionCommand`, `CancelCommand` assignment and the same
three event re-wirings. All eight `PackageListView` dependency properties are unchanged, so
every parent page binding still works.

### WinUI 3 API differences hit while converting
`ItemPrepared` and `RealizedItems()` are UWP-only and do not exist in WinUI 3, and
`ListView` has no settable `ItemsPanelTemplate` (it is `ItemsControl.ItemsPanel`), and
`ContainerContentChangingEventArgs` has no `ItemContainerPrepared` event. The build errors
drove these choices rather than guesswork.

## Still outstanding — not done

Honest list of everything from the original brief that remains:

1. **34 of 37 pages have not been remade.** Only the Apps page, Uninstaller page and
   Installer page row templates have been touched. Untouched pages include
   `SnipPage` (826), `OverclockSection` (679), `NetworkPage` (500), `AffinityPage` (492),
   `ThreadTunerPage` (368), `BiosManagerPage` (349), `GpuDriversPage` (258),
   `PowerPlansPage` (262), `PriorityBoostsPage` (225), `SettingsPage` (266),
   `PersonalizePage` (140), `ReservedCpuSetsPage`, `WindowsSettingsHubPage`,
   `GraphicsHubPage`, `OverclockPage`, all `PackageManager` sub-pages, all four Snip windows,
   and the dialogs `RuleEditorDialog`, `ThreadListDialog`, `NvidiaComponentPickerDialog`.
2. **~1,700 `FontSize` attributes** still set fixed sizes instead of the stock type ramp.
   The Uninstaller page and these two install templates were converted; the rest were not.
3. **~160 literal `CornerRadius` values** remain on other pages (26 were fixed on the
   Uninstaller page, 1 here).
4. **~90 hardcoded colour constructions in C#** — `BenchmarkPage.xaml.cs` went with the
   benchmark removal, which cleared 20 of them; `SnipOverlayWindow.xaml.cs` (19),
   `MainWindow.xaml.cs` (12), `OverclockSection.xaml.cs` (9), `SnipViewModel` (7) and the
   rest are untouched. These sit in canvas chart and annotation drawing code that rule 2
   protects.
5. **Dialogs** — 22 `ContentDialog` hosts not yet reviewed for stock conformance.
6. **Font preference feature is inert for body text** — `FontPreference.cs` is untouched and
   its resource keys survive, but nothing applies the font to stock controls. Product call
   still open.
7. **No visual verification.** The app has never been run in this session. Nothing in this
   report asserts light/dark/high-contrast, scaling, focus or hover appearance.

## Recommended order for the remaining work
`SnipPage`, `NetworkPage`, `AffinityPage`, `ThreadTunerPage` and `GpuDriversPage` are the
five largest untouched pages and would give the most visible change per commit. Then the
typography sweep across the remaining files, then the C# colour extraction in the chart and
overlay drawing code, then the dialogs. Each as its own commit with a green build.

---

# Addendum 4 — I redesigned the wrong page (correction)

## The mistake

For three rounds I reported "the Apps page is done" while describing
`PackageManager/Views/PackageManagerPage.xaml`. **That page is never displayed.**

`Services/Navigation/NavigationService.cs:26`:

```
PageKey.Apps => typeof(Pages.InstallerPage),
```

The rail's **Apps** item opens `Pages/InstallerPage.xaml`. A grep for `PackageManagerPage`
across all `.cs` files returns nothing outside its own file — no navigation target, no
`Frame.Navigate`, no reference of any kind. It is unreachable dead UI.

So every Apps claim in `a3745aa`, `aefef26` and `b5f8bec` was applied to a page the user can
never open, which is exactly why the layout looked unchanged. Only the direct Install/Update
button in `433470f` and this commit touched the page that is actually on screen.

**Correction to earlier claims in this report:** "Apps page redesigned", "Apps page
restocked" and "Apps rows converted to ListView" all refer to the unreachable
`PackageManagerPage`. The one-click row button and the new responsive layout below are the
only Apps-page work that is user-visible.

## `PackageManagerPage` — now confirmed dead code

`PackageManagerPage.xaml` plus its five child pages (`DiscoverPackagesPage`,
`InstalledPackagesPage`, `SoftwareUpdatesPage`, `PackageBundlesPage`,
`PackageManagersPage`) form a complete, unreferenced Apps section — roughly 640 lines of XAML
and their ViewModels, none of it reachable. This is the **third** latent unreachable-feature
bug found in this project, after the meteor resource keys and the two dead tag mappings.

It is **not** deleted here. `b5f8bec` converted its `PackageListView` host to a `ListView`,
which is correct code either way, but wiring this whole section back up, or deleting it, is a
product decision about package-manager scope. Logged as review item 5.

## Apps page actually redesigned — `508919e`

`Pages/InstallerPage.xaml`, the page the rail actually opens.

**Layout before:** one vertical `StackPanel` (`Spacing="32"`, `MaxWidth="1064"`,
`HorizontalAlignment="Left"`) holding the show-installed toggle, an `InfoBar`, then four
full-width category bands stacked top to bottom — Web Browsers, Game Launchers, Socials &
Communication, Utilities — each a `SubtitleTextBlockStyle` heading above a
`UniformGridLayout` card grid plus a parallel compact list. One long scroll, headings
separated by 32px of empty space, no grouping, and a hard 1064px cap that left the right
half of a wide window empty.

**Layout after:** a responsive two-column category dashboard.

- `CategoryGrid` — a `Grid` with two named columns and `RowSpacing`/`ColumnSpacing` 16.
- **Row 0** — a `CardBorder` holding the show-installed `ToggleSwitch` with its label, plus
  the `NothingToInstall` `InfoBar`. The toggle previously floated above a 32px gap with its
  caption as a separate `CaptionTextBlockStyle` line; it is now a labelled switch in a card.
- **Rows 1–2** — the four categories as `CardBorder` cards in a 2×2 arrangement:
  Browsers + Launchers on row 1, Socials + Utilities on row 2.
- **Adaptive:** `VisualStateGroup` with `WideCategoryLayout` gated by
  `AdaptiveTrigger MinWindowWidth="1100"`. Above 1100 effective pixels the second column is
  `*` and the Launchers and Utilities cards sit in it; below it the second column collapses to
  `0` and all four cards stack in column 0. The XAML-declared values are the narrow layout,
  so the default state needs no trigger.
- `MaxWidth` raised 1064 → 1400 so the two-column layout has room.

### Before / after

| Before | After |
|---|---|
| `StackPanel Spacing="32" MaxWidth="1064" HorizontalAlignment="Left"` | `CategoryGrid` `Grid`, `RowSpacing`/`ColumnSpacing` 16, `MaxWidth="1400"`, full width |
| 4 full-width bands stacked vertically | 2×2 `CardBorder` category cards |
| `SubtitleTextBlockStyle` headings floating in empty space | headings inside their cards, 12px from the card content |
| toggle + caption as loose rows above an `InfoBar` | labelled switch in a `CardBorder`, `InfoBar` beneath it |
| fixed 1064px cap, right half of wide windows empty | responsive 2-column / 1-column, breakpoint 1100 |
| no adaptivity at all | `AdaptiveTrigger` + `VisualStateGroup` |

### Preserved exactly
All eight repeater `x:Name`s (`BrowserGrid`, `BrowserList`, `LauncherGrid`, `LauncherList`,
`SocialGrid`, `SocialList`, `UtilityGrid`, `UtilityList`) and every page-level `x:Name`
(`RootGrid`, `ViewModePanel`, `ViewCardBtn`, `ViewCompactBtn`, `MainSelectorBar`, `TabInstall`,
`TabUninstall`, `InstallPanel`, `UninstallPanel`, `BrowserDetailsDialog`, `DialogStatusText`,
`DialogProgressBtn`). Every `x:Bind`, every `ItemsSource`, both item templates, the
`UniformGridLayout` parameters and the view-mode radio buttons are unchanged. The
`UninstallerPage` host and `BrowserDetailsDialog` are untouched. The build validates every
XAML-referenced handler, and it is green.

### WinUI 3 API errors hit while building this
`VisualStateManager.Setters` / `VisualStateSetter` are not usable on a `Grid` in this SDK
(`WMC0010`, `WMC0001`), and `AdaptiveTrigger` has no `MaxWindowWidth` (`WMC0011`) — only
`MinWindowWidth`. The classic `VisualStateGroup` + `StateTriggers` form is what works here, so
the breakpoint is expressed as "wide above 1100" with narrow as the XAML default rather than
as an explicit narrow state.

## Review items added
5. **`PackageManagerPage` and its five child pages are unreachable.** Decide: wire the
   package-manager section into the nav, or delete it. Currently it is ~640 lines of XAML plus
   ViewModels that no user can reach.

---

# Addendum 5 — Crash fix, empty space, Uninstaller restock

## Commits
| Commit | Contents |
|---|---|
| `5ac198b` | Register PersonalizeViewModel in DI to fix Personalize page crash |
| `3da2aa5` | Stop category cards stretching and tighten tile size on Apps page |
| `349f382` | Restock Uninstaller Remover and Startup panels and convert typography to stock type ramp |

Debug x64 build **0 errors, 0 warnings**.

## Crash fixed — `PersonalizePage` and `SettingsPage` — `5ac198b`

### How it was found
`crash.log` in the repo root is stale (Oct 7). `App.xaml.cs:88` writes real crashes to
`%LOCALAPPDATA%\kaliteConfig\logs\crash.log`, and that file was **empty** and last written
Oct 13 — so the current failure was not reaching the `UnhandledException` handler. Guessing
from the source was not working, so a temporary probe was added to `MainWindow` that drove the
real navigation path (`NavView.SelectedItem`) through the rail and logged each outcome. The
probe was removed again in the same pass; `git status` is clean apart from the fix.

### The bug
`Pages/PersonalizePage.xaml.cs:28`

```csharp
ViewModel = App.Services.Get<PersonalizeViewModel>();
```

`ServiceCollectionExtensions.Get<T>()` (`ServiceCollectionExtensions.cs:61`) returns
`default` for an unregistered type, so `ViewModel` was `null` and the very next statement

```csharp
ViewModel.PropertyChanged += ViewModel_PropertyChanged;
```

threw `NullReferenceException` in the constructor. Captured verbatim:

```
[ERR] PersonalizePage: TargetInvocationException
 ---> System.NullReferenceException: Object reference not set to an instance of an object.
   at kaliteConfig.Pages.PersonalizePage..ctor() ... PersonalizePage.xaml.cs:line 31
```

`PersonalizeViewModel` was the **only** page ViewModel missing from the DI list — the other
twelve (`AffinityViewModel`, `NetworkViewModel`, `InstallerViewModel`, `PowerPlansViewModel`,
`ThreadTunerViewModel`, `SnipViewModel`, `BiosManagerViewModel`, `GpuDriversViewModel`,
`UninstallerViewModel`, `UpdateViewModel`, `EtwManagerViewModel`,
`ReservedCpuSetsViewModel`, `WindhawkProvisioningViewModel`) are all registered. Its four
constructor dependencies (`IThemeSettingsService`, `ICursorService`, `IWallpaperService`,
`WindowsCapabilityService`) were already registered; only the ViewModel was not.

Fix: `s.AddTransient<PersonalizeViewModel>();`

Post-fix probe through the real rail navigation:

```
[03:03:06] Settings navigated OK
[03:03:08] PersonalizePage navigated OK
[03:03:09] AppsPage navigated OK
```

### This was pre-existing, not caused by the remaster
Nothing in this branch touched `PersonalizePage`'s constructor, its ViewModel, or the DI
registrations. `PersonalizePage` would have thrown the same way before the remaster. The
reported "settings" crash was the same stale-build confusion as the Apps page — once the DI
fix was in, both Settings and Personalize navigate cleanly.

## Empty space on Apps page — `3da2aa5`

Cause: the four category cards sat in a 2×2 `Grid` with `Auto` rows. `Border` defaults to
`VerticalAlignment="Stretch"`, so **Launcher and Utilities were stretched to the height of the
tallest card in their row** (Web Browsers / Socials), leaving a large empty region inside each
short card.

Fix:
- `VerticalAlignment="Top"` on `BrowserCard`, `LauncherCard`, `SocialCard`, `UtilityCard` —
  each card now hugs its content.
- `UniformGridLayout` `MinItemWidth` 184 → 160 and `MinItemHeight` 192 → 176 on all four
  categories, so tiles are denser and the tall categories are shorter.

## Uninstaller page restocked — `349f382`

The earlier pass only covered the Uninstaller tab's main app list. The Remover and Startup
panels were still fully custom, and 89 `TextBlock` elements across the whole page still set
fixed `FontSize` values.

**Typography:** all **79** `TextBlock` `FontSize` attributes converted to the stock type ramp
(`9/10/11/12` → `CaptionTextBlockStyle`, `13/14/15` → `BodyTextBlockStyle`,
`16/17` → `BodyStrongTextBlockStyle`, `18/19/20` → `SubtitleTextBlockStyle`,
`22+` → `TitleTextBlockStyle`). The 7 `FontIcon` `FontSize` values were left alone — icon
glyph size is not text typography.

**Remover and Startup panels:** 8 `Border`s carrying hand-written
`CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush` + `BorderThickness="1"`
+ `CornerRadius` were replaced with `Style="{StaticResource CardBorder}"`, plus one more on
the Startup panel.

**Page totals now:** `TextBlock FontSize` **0**, literal `CornerRadius` **0**, hardcoded hex
**0**, leftover `RemoverSectionTitle`/`RemoverCaption`/`RemoverFlatListItemStyle` **0**. All 15
`x:Name`s intact.

## Still outstanding
Unchanged from Addendum 3: 32 pages not remade, ~1,600 `FontSize` attributes and ~160 literal
`CornerRadius` on other pages, ~90 hardcoded colours in C# drawing code, 22 dialogs, the inert
font-preference feature, and no light/dark/high-contrast/scaling verification.

---

# Addendum 6 — COM exception, install card layout, snip tray blocking call

## Commits
| Commit | Contents |
|---|---|
| `96a987d` | Fix install card layout: stop action button overlaying card and remove fixed tile height |
| `4a2d71e` | Stop blocking UI thread on WinRT encoder during snip tray drag start |

Debug x64 build **0 errors, 0 warnings**.

## COM exception `0x8001010D` — `4a2d71e`

```
System.Runtime.InteropServices.COMException
  Message=Retrieving the COM class factory for component with CLSID
  {C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD} failed ... 8001010d
  An outgoing call cannot be made since the application is dispatching
  an input-synchronous call. (RPC_E_CANTCALLOUT_ININPUTSYNCCALL)
```

### Root cause
`Windows/SnipTrayWindow.xaml.cs`, `OnDragStarting` — a **drag-start input handler** — blocked
the UI thread on a WinRT call:

```csharp
var encoderTask = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask();
encoderTask.Wait();          // UI thread blocks
var encoder = encoderTask.Result;
encoder.SetSoftwareBitmap(_img);
var flushTask = encoder.FlushAsync().AsTask();
flushTask.Wait();            // UI thread blocks
```

`BitmapEncoder.CreateAsync` is a WinRT async whose activation and completion must be pumped by
the UI thread's STA message loop. Calling `.Wait()` from inside a handler that the dispatcher
is already servicing as an input-synchronous call means the outgoing COM call is made while
the apartment is mid-dispatch — precisely `RPC_E_CANTCALLOUT_ININPUTSYNCCALL`. The empty
`catch (Exception)` swallowed the failure, so the drag simply carried no thumbnail.

The CLSID was checked in the registry: `{C2CF3110-…}` carries only an `AppId` and no
`InprocServer32`, i.e. an in-process WinRT/Immersive class rather than a normal COM server,
which is consistent with a WinRT async activation.

### Fix
The thumbnail is now encoded **ahead of the drag** and cached, so `OnDragStarting` only reads
it:

- `PrepareDragBitmap()` — starts `EncodeDragBitmapAsync()` once per image.
- `EncodeDragBitmapAsync()` — `await`s `BitmapEncoder.CreateAsync` and `FlushAsync` properly
  instead of blocking, then stores a `RandomAccessStreamReference` in `_dragBitmap`.
- `OnPointerPressed` calls `PrepareDragBitmap()`, so encoding is already in flight before the
  user can start dragging.
- `OnDragStarting` calls `PrepareDragBitmap()` as a backstop and sets the bitmap only if the
  cached reference exists. No blocking call remains on the input path.

No other `.Wait()` / `.Result` on a UI-thread path remains in `Windows/` or `Controls/`
(verified by grep).

This bug is **pre-existing** and not introduced by the remaster.

### Note on diagnosis
No new entry appeared in `%LOCALAPPDATA%\kaliteConfig\logs\crash.log`, so this is a
**first-chance** exception that the VS debugger breaks on rather than a fatal crash. If VS is
configured to break on all COM exceptions, Debug → Windows → Exception Settings →
"COMException" can be set to *Do not break* to confirm the app itself is healthy. If the
exception occurs somewhere other than a snip-tray drag, the page and action are needed to pin
it — this fix addresses the one call site in the codebase that provably matches the error.

## Install card layout fixed — `96a987d`

Two defects, both visible on the Apps page.

**Floating "extra install button".** The card template root was a single `Grid` whose
`CardRoot` button filled the whole grid, with `RowAction` and `RowBusy` positioned
`VerticalAlignment="Bottom"` **on top of it**. The action button rendered as a stray floating
button over the card rather than as part of the row.

**Empty room.** `UniformGridLayout MinItemHeight="176"` with `ItemsStretch="None"` gave every
tile at least 176px, while `CardRoot` was `VerticalAlignment="Stretch"`, so a card whose
content is ~130px was padded out to 176px with dead space.

Fixes:
- Template root is now a two-row `Grid`: row 0 the card button (`VerticalAlignment="Top"`, so
  it hugs its content), row 1 the action button / busy ring, with `RowSpacing` 6 between them.
  Nothing overlaps any more.
- `CardRoot`'s redundant inner `Grid Padding="14"` removed; the `StackPanel` `Spacing` went
  10 → 8, and the status text is now a direct `TextBlock` instead of a wrapping `Grid`.
- The card name gained `TextTrimming="CharacterEllipsis"` so long app names no longer force the
  tile wider.
- `MinItemHeight` set to `0` on all four category grids so tiles size to their content.
- Tile `Margin` 8 → 12 to keep vertical rhythm between tiles now that heights vary.

## Outstanding
Unchanged: 32 pages not remade, ~1,600 `FontSize` and ~160 literal `CornerRadius` elsewhere,
~90 hardcoded colours in C# drawing code, 22 dialogs, inert font preference, and no
light/dark/high-contrast/scaling verification.

---

# Addendum 7 — Install cards regression and picker COM error

## Commits
| Commit | Contents |
|---|---|
| `4760d47` | Restore install tile height and defer wallpaper picker activation out of input dispatch |

Debug x64 build **0 errors, 0 warnings**.

## Install cards disappeared — my regression, reverted

In `96a987d` I set `UniformGridLayout MinItemHeight="0"` on all four category grids,
reasoning that it would let tiles size to their content. **That was wrong.**
`UniformGridLayout` derives item height from `MinItemHeight` when no explicit height exists,
so `0` collapsed every tile to zero height and the cards vanished entirely.

Reverted to `MinItemHeight="176"` on all four categories. The parts of `96a987d` that were
correct are kept:

- The card template root is a two-row `Grid`: row 0 `CardRoot` (`VerticalAlignment="Top"`),
  row 1 the action button / busy ring. Nothing overlaps.
- The outer `Grid` has `RowSpacing="6"`, so the action button sits directly under the card
  rather than floating over it.
- Card name uses `TextTrimming="CharacterEllipsis"`.

`MinItemWidth` remains `160` and `MinItemHeight` `176`, which is the pair that rendered
correctly in `3da2aa5` when the user could still see the cards.

**I could not visually verify the tile geometry** — there is no display in this environment,
so I can only confirm the build, the markup and the restored values. If the tiles still look
wrong, a screenshot is the only way I will stop guessing at it.

## `RPC_E_CANTCALLOUT_ININPUTSYNCCALL` when adding an image — `4760d47`

**Status: mitigation applied, root cause NOT confirmed.** No stack trace was available, so
this is a best-effort change, not a verified fix.

### What is known
- Trigger: adding an image on `PersonalizePage` (`AddWallpaper_Click`).
- No entry appears in `%LOCALAPPDATA%\kaliteConfig\logs\crash.log`, so this is a
  first-chance exception the debugger breaks on, not a fatal crash.
- The picker code itself is correct: it constructs the `FileOpenPicker`, calls
  `InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow))`
  (required for an unpackaged app), adds the extension filters and awaits
  `PickSingleFileAsync`.
- There are **20** picker call sites across the app and the user reports only this one
  failing, so the general pattern is not the problem.

### The change
`AddWallpaper_Click` no longer activates the picker directly inside the click handler. The
handler is now a plain `void` that posts the work to `DispatcherQueue.TryEnqueue(async ...)`,
so picker activation happens after the current input dispatch has completed rather than
during it. A `try/catch` now wraps the body so a failure is contained instead of propagating
out of the async lambda.

```csharp
private void AddWallpaper_Click(object sender, RoutedEventArgs e)
{
    ...guards...
    DispatcherQueue.TryEnqueue(async () => { /* build picker, await PickSingleFileAsync */ });
}
```

The observable behaviour is unchanged: same picker type, same `ViewMode`, same
`SuggestedStartLocation`, same extension filters, same `AddWallpaperAsync` + `UpdatePreview`
on success, same silent return when the user cancels.

### What is still needed
The stack trace. "Cannot evaluate the exception stack trace" means VS cannot walk it for this
exception, so the caller cannot be identified from what was pasted. To pin it:

1. Set a breakpoint on `first chance` `System.Runtime.InteropServices.COMException` at
   kaliteConfig.dll only, or
2. Open **Debug → Windows → Output** and find the call site, or
3. Note the exact steps — which page, which button, and whether the window had just been
   activated or a timer was running.

One specific suspicion worth checking: `PersonalizePage.OnWindowActivated` calls
`RefreshFromSystem()` → `ViewModel.Reload()` on **every window activation**, and the page sets
`NavigationCacheMode="Enabled"`. If `Reload()` is doing COM work (file enumeration or
thumbnail decode) when Add is pressed, that would produce exactly this error. It is a
candidate, not a confirmed cause.

## Outstanding
Unchanged: 32 pages not remade, ~1,600 `FontSize` and ~160 literal `CornerRadius` elsewhere,
~90 hardcoded colours in C# drawing code, 22 dialogs, inert font preference, and no visual
verification of any page.

---

# Addendum 8 — Root cause of the picker COM error, and measured verification of install cards

## Commit
| Commit | Contents |
|---|---|
| `24cc786` | Stop wallpaper COM re-activation during system file picker on Personalize page |

Debug x64 build **0 errors, 0 warnings**.

## `RPC_E_CANTCALLOUT_ININPUTSYNCCALL` — actual root cause found

The earlier `4760d47` change (deferring picker activation via `DispatcherQueue`) was a
**mitigation, not the fix**. The real cause is a different call entirely.

### Chain

1. `PersonalizePage.OnWindowActivated` calls `RefreshFromSystem()` on **every** window
   activation.
2. `RefreshFromSystem()` → `PersonalizeViewModel.Reload()` (`PersonalizeViewModel.cs:61`).
3. `Reload()` line 76 → `_wallpaper.GetMonitors()`.
4. `WallpaperService.GetMonitors()` → `new DesktopWallpaperApi()`.
5. `DesktopWallpaperInterop.cs:58` → `Activator.CreateInstance(type)` — a **CoCreateInstance**
   of the `IDesktopWallpaper` COM class, which is CLSID
   **`{C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD}`**, matching the reported error exactly.

Opening a `FileOpenPicker` deactivates and reactivates the host window. That activation
arrives while the dispatcher is still servicing the input-synchronous click, so the
`CoCreateInstance` inside `Reload()` is an outgoing COM call made during an input-synchronous
dispatch — `RPC_E_CANTCALLOUT_ININPUTSYNCCALL`. The picker itself was never the problem, and
neither was `BitmapDecoder` in `WallpaperService.AddToLibraryAsync`.

### Fix — `24cc786`

- New `bool _systemPickerOpen` field.
- `OnWindowActivated` returns early while `_systemPickerOpen` is true, so activating or
  deactivating the window around a picker can no longer drive a COM activation.
- `AddWallpaper_Click` sets `_systemPickerOpen = true` before `PickSingleFileAsync()` and
  clears it in a `finally`. `PickCursorFolder_Click` does the same.
- `RefreshFromSystem()` wraps `ViewModel.Reload()` in a `try/catch`, so a transient COM
  failure can no longer take down the page even if some other path re-enters it.
- The `DispatcherQueue.TryEnqueue` deferral from `4760d47` is kept: it is harmless and still
  avoids activating the picker from inside the click dispatch.

Behaviour is unchanged — the monitors still refresh on activation once the picker closes.

## Install cards — measured, not assumed

To stop guessing about "cards are gone", the live visual tree was measured at runtime. A
temporary probe navigated the frame to `InstallerPage`, walked the tree, and logged each
realized item's `ActualWidth`/`ActualHeight`. It was removed afterwards; `git status` is clean
apart from the fix.

```
page=InstallerPage contentSize=1355x704
ItemsRepeater realizedChildren=3  item[0] Grid 160x164 vis=Visible
                                  item[1] Grid 160x164 vis=Visible
                                  item[2] Grid 160x164 vis=Visible
ItemsRepeater realizedChildren=5  item[0..4] Grid 160x164 vis=Visible
ItemsRepeater realizedChildren=0  visible=Collapsed   <- compact list variant, card mode
SUMMARY repeaters=9 items=11 zeroSized=0
```

- **11 realized tiles, `zeroSized=0`** — every tile has real geometry. The cards are present
  and sized; the `MinItemHeight="0"` regression is genuinely gone.
- Content width **1355** is above the **1100** breakpoint, so `WideCategoryLayout` is active
  and the two-column layout is in effect.
- The `Collapsed` repeaters are the compact-list variants, which are correctly hidden while
  card view mode is active.
- Repeaters reporting `0` realized but `Visible` are the categories whose items are filtered
  out by the current `ShowInstalled` / status filters, not a layout fault.

This confirms the tile geometry numerically. It does **not** confirm how the page *looks* —
no rendering is available in this environment.
