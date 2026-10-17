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
| Step 1: inventory (`BUTTON-INVENTORY.md`) | in progress |
| Step 2: nav guard (`Themes/SideNavResources.xaml`) | not started |
| Step 3: global button layer in `Themes/KaliteTheme.xaml` and `App.xaml` | not started |
| Step 4: per-page stock look (Pages/) | not started |
| Step 4: per-page stock look (GpuOverclock/, PackageManager/, Controls/) | not started |
| Step 4: per-page stock look (Snip windows and controls) | not started |
| Step 5: merge paired buttons | not started |
| Step 6: remove hover/press animations | not started |
| Step 7: button lookalikes | not started |
| Step 8: dialogs to ContentDialog/Flyout | not started |
| Static checks and final build | not started |
| `BUTTON-REPORT.md` | not started |

## Status legend
not started / in progress / committed

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
