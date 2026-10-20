# Personalize: plan

## Project facts
- Target: net10.0-windows10.0.22621.0, TargetPlatformMinVersion 10.0.17763.0
- Windows App SDK 2.4.0, WinUI 3, unpackaged (WindowsPackageType=None), so HKCU writes are not virtualized by MSIX.
- No CsWin32. Interop is hand-written in one file: `src/kaliteConfig/Services/Personalization/NativeMethods.cs`.
- DI: `Services/DependencyInjection/ServiceCollectionExtensions.cs` (AddSingleton). App.xaml.cs exposes properties via Services.GetRequiredService.
- Nav: `MainWindow.xaml` NavigationViewItem with Tag="<Key>Page"; `Services/Navigation/NavigationService.cs` maps PageKey -> page type and Tag -> PageKey.
- Localization: no .resw files exist in the project. Strings are inline, so new strings are inline too (flagged as a deviation from the brief, see PERSONALIZE-REVIEW).
- CommunityToolkit.WinUI.Controls.SettingsControls 8.2.251219 is already referenced; not used for the new page to keep the change small.
- Baseline (commit 74f3e40): build 0 errors, 14 warnings.

## Scope actually built this run (Tier 1 subset)
| Area | Setting | API / key | Status | Min build | Explorer restart |
|---|---|---|---|---|---|
| Wallpaper | image path | IDesktopWallpaper.SetWallpaper | documented (Learn: shobjidl_core IDesktopWallpaper) | Win8 | no |
| Wallpaper | fit mode (Center/Tile/Stretch/Fit/Fill/Span) | IDesktopWallpaper.SetPosition, enum 0-5 | documented | Win8 | no |
| Wallpaper | background color | IDesktopWallpaper.SetBackgroundColor | documented | Win8 | no |
| Wallpaper | per-monitor list | GetMonitorDevicePathCount/At | documented | Win8 | no |
| Cursor | pointer speed 1-20 | SPI_SETMOUSESPEED (0x0071) | documented | Win2000 | no |
| Cursor | enhanced precision | SPI_SETMOUSE (0x0004), int[3] array, element 3 | array documented, element meaning community | all | no |
| Cursor | scroll lines | SPI_SETWHEELSCROLLLINES (0x0069) | documented | all | no |
| Cursor | restore default cursors | SPI_SETCURSORS (0x0057) | documented | all | no |
| Cursor | pointer size | HKCU Control Panel\Cursors CursorBaseSize | community | Win10 | yes (flagged off) |
| Colors | app mode light/dark | HKCU ...\Themes\Personalize AppsUseLightTheme | documented (Learn settings-common) | Win10 1809 | no |
| Colors | system mode light/dark | HKCU ...\Themes\Personalize SystemUsesLightTheme | documented | Win10 1809 | no |
| Colors | transparency effects | HKCU ...\Themes\Personalize EnableTransparency | documented | Win10 1809 | no |
| Colors | broadcast theme change | WM_SETTINGCHANGE lParam "ImmersiveColorSet" | community (flagged, see below) | Win10 | no |
| Colors | accent color | DWM AccentColor / ColorizationColor | community (flagged off) | Win10 | no |
| Colors | accent on title bars | DWM ColorPrevalence | documented | Win10 | no |

## Deferred / not built this run (Tier 2/3, and parts of Tier 1)
- Cursor schemes (.cur/.ani mapping), per-role override, "Keep these changes?" countdown dialog, cursor preview, text-cursor thickness, hide-while-typing, pointer trails, show-location-on-Ctrl.
- Wallpaper library with thumbnails, drag and drop, slideshow, recent items, Tier 1 monitor preview.
- Zip import/export (only JSON profiles).
- Taskbar, desktop, visual effects (Tier 2). Sound scheme and listeners (Tier 3).
- Test project: none exists, so no unit tests were added.

## Files
New:
- Services/Personalization/NativeMethods.cs
- Services/Personalization/PersonalizationSnapshot.cs (snapshot store plus apply/verify/rollback)
- Services/Personalization/WallpaperService.cs
- Services/Personalization/CursorSettingsService.cs
- Services/Personalization/ThemeSettingsService.cs
- Services/Personalization/PersonalizationProfileService.cs
- ViewModels/PersonalizeViewModel.cs
- Pages/PersonalizePage.xaml, Pages/PersonalizePage.xaml.cs

Edited (minimal):
- Services/DependencyInjection/ServiceCollectionExtensions.cs (register services)
- Services/Navigation/NavigationService.cs (PageKey.Personalize, one mapping per table)
- MainWindow.xaml (one nav item)
