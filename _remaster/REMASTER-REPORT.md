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
