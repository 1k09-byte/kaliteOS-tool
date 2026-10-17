# Button reset: progress

Branch: `style/stock-buttons` (from `master` @ cac1385)

## Baseline (step 0)
- Windows App SDK: 2.4.0 (from `src/kaliteConfig/kaliteConfig.csproj`)
- Target framework: `net10.0-windows10.0.22621.0`
- Baseline build: `dotnet build src/kaliteConfig/kaliteConfig.csproj` passes with 0 warnings, 0 errors
- Running the app and taking screenshots: not available in this environment. Visual checks are listed as a manual checklist in `BUTTON-REPORT.md`.

## Areas

| Area | Status |
|---|---|
| Step 0: branch, baseline, SDK record | committed |
| Step 1: inventory (`BUTTON-INVENTORY.md`) | committed |
| Step 2: nav guard (`Themes/SideNavResources.xaml`) | not started |
| Step 3: global button layer in `Themes/KaliteTheme.xaml` and `App.xaml` | not started |
| Step 4: per-page stock look (Pages/) | not started |
| Step 4: per-page stock look (GpuOverclock/, PackageManager/, Controls/) | not started |
| Step 4: per-page stock look (Snip windows and controls) | not started |
| Step 5: merge paired buttons | committed |
| Step 6: remove hover/press animations | committed |
| Step 7: button lookalikes | committed |
| Step 8: dialogs to ContentDialog/Flyout | committed (all button-opened dialogs are ContentDialog or Flyout; review items logged) |
| Static checks and final build | done (0 warnings, 0 errors; no TODO added) |
| `BUTTON-REPORT.md` | done (manual visual checks listed as limitations) |

## Step 5: merge paired buttons
- Status: committed
- Benchmark: Start capture + Stop merged into `ToggleCaptureCommand` (BenchmarkViewModel.cs). Label and glyph follow `IsCapturing`. Accent dropped, since the merged button cannot hold one accent in both states. BUTTON-REVIEW logged in BenchmarkPage.xaml and BenchmarkViewModel.cs.
- Speed test: Start + Cancel merged into `SpeedToggle_Click` (NetworkPage) with `SpeedButtonText` (NetworkViewModel). BUTTON-REVIEW logged in NetworkPage.xaml and NetworkPage.xaml.cs.
- ThreadList: Suspend/Resume was already one button (label follows `Suspended`). No change.
- Checked, not merged: PackageRow Action + Cancel (both visible while running), Apply/Cancel dialog pairs, ThreadTuner flyout Suspend/Resume (menu items, not a button pair).
- Other pairs: see BUTTON-REPORT.md.

## Step 6: hover/press animations
- Status: committed
- Snip tile favourite-star hover (`Tile_PointerEntered/Exited`) kept: it switches star visibility, not an animation. BUTTON-REVIEW logged in SnipPage.xaml.cs.
- Benchmark card `PointerPressed` kept: cards hold nested controls, so a Button would nest interactive children. BUTTON-REVIEW logged in BenchmarkPage.xaml.cs.

## Status legend
not started / in progress / committed

## Step 4: per-page stock look (accent cleanup, installer, lookalikes)
- Status: committed (9e5cdfb, 557e52d, 6925b81)
- 9e5cdfb: one accent button per view kept (overclock risk gate, pending-changes Keep, Gaming mode toggle, Update now, Update selection, New Bundle, New Plan, Start capture); other buttons use stock default.
- 557e52d: InstallerPage card style removed (unreferenced; had custom opacity/scale transitions).
- 6925b81: BiosManager value pill (Border+Tapped) and ThreadTuner row chevron (FontIcon+Tapped) converted to stock Button with Click handlers.

## Step 2: nav guard
- Status: committed
- Added src/kaliteConfig/Themes/SideNavResources.xaml (frozen copy of KaliteButton + implicit Button rule, SideNav* keys).
- MainWindow.xaml: NavigationView.Resources wrapped in ResourceDictionary and merges SideNavResources.xaml.
- Build: 0 warnings, 0 errors.

## Step 3: global button layer
- Status: committed
- KaliteTheme.xaml: removed KaliteButton, implicit Button style, KaliteSubtleButton, KaliteIconButton (incl. hover/press Storyboards, ControlTemplate), and KaliteButtonFill/Stroke(+Hover) brushes in Light/Dark/Default. ControlCornerRadius token kept (shared, stock value).
- FontPreference.cs: removed KaliteButton/Subtle/Icon and typeof(Button) from StyleKeys; buttons still follow the font via ContentControlThemeFontFamily (rewritten by FontPreference).
- RuleEditorDialog.xaml: removed PrimaryButtonStyle/CloseButtonStyle based on KaliteButton (stock ContentDialog buttons now).
- Build: 0 warnings, 0 errors.
