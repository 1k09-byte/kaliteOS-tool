// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
// App settings page (persisted via ThemeService).
// Written in the Windows App SDK C# dialect. See docs/GALLERY-REFERENCE.md section 2.

using System;
using System.Threading.Tasks;
using kaliteConfig.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Linq;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace kaliteConfig.Pages
{
    /// <summary>x:Bind helper: nullable download percent → ProgressBar value.</summary>
    public static class SettingsPageBindings
    {
        public static double UpdateProgressValue(double? percent) => percent ?? 0;
    }
    /// <summary>
    /// App settings page (persisted via ThemeService).
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        private readonly kaliteConfig.Services.StartupService _startup = new();
        private bool _syncingStartupToggle;
        // Update banner VM, populated by the startup update check.
        private readonly ViewModels.UpdateViewModel Vm = new();

        // ThemeServiceAttach restores the saved ComboBox selection during the
        // ComboBox's own Loaded, which raises SelectionChanged. Ignore those and
        // only record a Material the user actually picked.
        private static bool _appearanceReady;

        // Font picker: the ComboBox restores the saved choice on Loaded; until
        // that is done, SelectionChanged must not re-save what was just loaded.
        private bool _fontsReady;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;

            // App theme / Material are owned and persisted by DevWinUI's ThemeService:
            // the ComboBoxes are wired through ThemeServiceAttach, which restores the
            // saved choice on load and writes a new one on selection. The Material
            // default (None = solid black surface) is only applied while the user has
            // never picked one, so record the first change here.
            backdropMode.SelectionChanged += BackdropMode_SelectionChanged;

            // Seed the tint picker before the page is measured, so its own
            // "initialised" report carries the saved colour instead of its default.
            try { SeedTintPicker(); } catch { }
        }

        private void BackdropMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_appearanceReady) BackdropPreference.MaterialChosen = true;
        }

        // ── backdrop tint ────────────────────────────────────────────────────────
        //
        // Colour and strength are stored separately from the material so the
        // material setting stays exactly as DevWinUI left it. The window's tint
        // brush is updated live, so a change is visible without leaving the page.

        private (byte R, byte G, byte B) _tintColor = Services.BackdropTint.DefaultColor;
        private bool _tintReady;
        private bool _pickerOpen;

        /// <summary>
        /// Seeds the picker with the tint that is actually set. The ColorPicker
        /// reports its own default (black) the moment it is built, and that
        /// report is indistinguishable from a user drag - without this, the
        /// picker silently overwrites the user's tint with black on first load,
        /// which is exactly what happened: a 46%-strength BLACK wash over a black
        /// background reads as "the tint does nothing at all".
        ///
        /// ColorOf, not the raw channels: a profile with nothing stored reports
        /// None, whose channels are black, and seeding from those is the same bug
        /// one level up - it showed black, and then saved black back.
        /// </summary>
        private void SeedTintPicker()
        {
            var color = Services.BackdropTint.ColorOf(Services.BackdropTint.Load());
            TintPicker.Color = Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);
        }

        /// <summary>
        /// Restores the saved tint into the swatch and the slider. The colour is
        /// the app's accent blue until the user picks one, so the control has
        /// something to show instead of black-on-black.
        /// </summary>
        private void LoadTintIntoUi()
        {
            var tint = Services.BackdropTint.Load();
            _tintColor = Services.BackdropTint.ColorOf(tint);

            _tintReady = false;
            try
            {
                TintStrength.Value = Services.BackdropTint.StrengthPercentFor(tint);
            }
            finally
            {
                _tintReady = true;
            }

            RefreshTintVisuals();
        }

        private void RefreshTintVisuals()
        {
            // The swatch shows the tint at its current strength: what the window
            // is painted with, not just the hue.
            int strength = (int)TintStrength.Value;
            byte alpha = (byte)Math.Round(strength / 100.0 * Services.BackdropTint.MaxAlpha);
            var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, _tintColor.Item1, _tintColor.Item2, _tintColor.Item3));
            TintSwatch.Background = brush;
            TintStrengthText.Text = strength == 0 ? "Off" : $"{strength}%";
        }

        private void PushTintToWindow()
        {
            int strength = (int)TintStrength.Value;
            Services.BackdropTint.Save(_tintColor.Item1, _tintColor.Item2, _tintColor.Item3, strength);
            (App.MainWindow as MainWindow)?.ApplyBackdropTint(
                Services.BackdropTint.Load());
        }

        /// <summary>
        /// Opens the picker on the tint that is actually set. Without this the
        /// ColorPicker would show its own default (white) and a single click on
        /// the spectrum would jump the app from any hue to white.
        /// </summary>
        private void TintFlyout_Opened(object sender, object e)
        {
            // Re-seed first (the picker keeps its last state), and only then arm
            // the handler: the seeding itself raises ColorChanged.
            SeedTintPicker();
            _pickerOpen = true;
        }

        private void TintFlyout_Closed(object sender, object e)
        {
            _pickerOpen = false;
        }

        private void TintPicker_ColorChanged(object sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs e)
        {
            // Only a change the user actually made, in a picker that is open.
            // The picker also reports its default while it is being built and
            // while it is re-seeded on open, and those are not choices.
            if (!_tintReady || !_pickerOpen) return;

            _tintColor = (e.NewColor.R, e.NewColor.G, e.NewColor.B);
            // A fresh colour at 0% strength would look like nothing happened.
            if (TintStrength.Value == 0) TintStrength.Value = 25;
            PushTintToWindow();
            RefreshTintVisuals();
        }

        private void TintStrength_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_tintReady) return;
            PushTintToWindow();
            RefreshTintVisuals();
        }

        private void TintClear_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            Services.BackdropTint.Clear();
            (App.MainWindow as MainWindow)?.ApplyBackdropTint(Services.BackdropTint.None);

            // Clear resets the STRENGTH, not the chosen hue. Resetting the hue
            // too would leave the picker on black again, so the next drag saved
            // another invisible black wash - the very bug this control had.
            _tintColor = Services.BackdropTint.DefaultColor;
            SeedTintPicker();

            _tintReady = false;
            try { TintStrength.Value = 0; }
            finally { _tintReady = true; }
            RefreshTintVisuals();
        }

        // ── background image ────────────────────────────────────────────────────────

        private bool _bgReady;
        private string _savedStretch = "UniformToFill";
        private string _currentPath = string.Empty;

        /// <summary>
        /// Restores the saved image state into the controls (called on page
        /// load). Without this the card always showed "No image chosen" with
        /// a blank stretch box after a restart, even while the window was
        /// happily showing the saved picture.
        /// </summary>
        private void LoadBackgroundImageIntoUi()
        {
            _bgReady = false;
            try
            {
                var saved = BackgroundImageService.Load();
                bool hasImage = !string.IsNullOrWhiteSpace(saved.Path);
                _currentPath = hasImage ? saved.Path : string.Empty;
                _savedStretch = hasImage && !string.IsNullOrWhiteSpace(saved.Stretch)
                    ? saved.Stretch : "UniformToFill";
                BgImageName.Text = hasImage
                    ? System.IO.Path.GetFileName(saved.Path) : "No image chosen";
                // No image: park the slider at 80 (the fresh-pick default), not
                // the None sentinel 0 - a pick saves the slider value, and 0
                // would make the new picture invisible on arrival.
                BgOpacity.Value = hasImage ? saved.Opacity : 80;
                BgOpacityText.Text = $"{(int)BgOpacity.Value}%";
                var match = BgStretch.Items.Cast<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals((string?)i.Tag, _savedStretch,
                        StringComparison.OrdinalIgnoreCase));
                BgStretch.SelectedItem = match
                    ?? BgStretch.Items.Cast<ComboBoxItem>().First(i => (string?)i.Tag == "UniformToFill");
            }
            finally { _bgReady = true; }
        }

        private async void BgPick_Click(object sender, RoutedEventArgs e)
        {
            // async void: anything thrown after the await lands on the
            // dispatcher as an unhandled exception and takes the whole app
            // down, so the picker call is fully guarded.
            try
            {
                var path = await PickImageFileAsync();
                if (string.IsNullOrWhiteSpace(path)) return;

                // Validate the file exists before trying to use it
                if (!System.IO.File.Exists(path))
                {
                    BgImageName.Text = "File not found";
                    return;
                }

                BackgroundImageService.Save(path, (byte)BgOpacity.Value, _savedStretch);
                (App.MainWindow as MainWindow)?.ApplyBackgroundImage();

                _bgReady = false;
                try { BgImageName.Text = System.IO.Path.GetFileName(path); _currentPath = path; }
                finally { _bgReady = true; }
            }
            catch (Exception ex)
            {
                // Log the error and don't crash
                System.Diagnostics.Debug.WriteLine($"Background image picker error: {ex.Message}");
                BgImageName.Text = "Error loading image";
            }
        }

        /// <summary>
        /// Window handle for the pickers, or 0 when the window is not up yet
        /// (the Win32 fallback then uses the foreground window).
        /// </summary>
        private static nint BgPickerHwnd =>
            App.MainWindow is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0;

        /// <summary>
        /// Picks an image file. This app runs elevated (requireAdministrator
        /// manifest) and the brokered WinUI picker throws E_ACCESSDENIED in an
        /// elevated process, which - unhandled inside an async void handler -
        /// crashed the app on every click. So: try the WinUI picker, and on any
        /// failure fall back to the Win32 IFileDialog, which works elevated.
        /// Returns null when the user cancels or both dialogs fail.
        /// </summary>
        private static async Task<string?> PickImageFileAsync()
        {
            // Try Win32 picker first - more compatible across different Windows configurations
            try
            {
                System.Diagnostics.Debug.WriteLine("Attempting Win32 picker (primary)");
                var result = Win32FilePicker.PickOpenFile(BgPickerHwnd,
                    new[]
                    {
                        ("Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)",
                            "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"),
                        ("All files (*.*)", "*.*"),
                    },
                    "Choose a background image");
                if (!string.IsNullOrEmpty(result))
                {
                    System.Diagnostics.Debug.WriteLine($"Win32 picker succeeded: {result}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Win32 picker failed: {ex.Message}");
            }

            // Fallback to WinUI picker
            try
            {
                System.Diagnostics.Debug.WriteLine("Attempting WinUI picker (fallback)");
                var picker = new Windows.Storage.Pickers.FileOpenPicker
                {
                    ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
                };
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".gif");
                picker.FileTypeFilter.Add(".webp");

                var hwnd = BgPickerHwnd;
                if (hwnd != nint.Zero)
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                else
                    System.Diagnostics.Debug.WriteLine("BgPickerHwnd is zero, WinUI picker may not work");

                var file = await picker.PickSingleFileAsync();
                System.Diagnostics.Debug.WriteLine($"WinUI picker result: {file?.Path ?? "null (cancelled)"}");
                return file?.Path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WinUI picker failed: {ex.Message}");
                return null;
            }
        }

        private void BgRemove_Click(object sender, RoutedEventArgs e)
        {
            BackgroundImageService.Clear();
            (App.MainWindow as MainWindow)?.ApplyBackgroundImage();

            _bgReady = false;
            try
            {
                BgImageName.Text = "No image chosen";
                _currentPath = string.Empty;
                if (BgStretch.Items.Cast<ComboBoxItem>().Any(i => (string?)i.Tag == "UniformToFill"))
                    BgStretch.SelectedItem = BgStretch.Items.Cast<ComboBoxItem>().First(i => (string?)i.Tag == "UniformToFill");
                BgOpacity.Value = 80;
                _savedStretch = "UniformToFill";
            }
            finally { _bgReady = true; }
        }

        private void BgOpacity_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_bgReady) return;

            byte newValue = (byte)Math.Clamp((int)e.NewValue, 0, 100);
            BackgroundImageService.Save(_currentPath, newValue, _savedStretch);

            // Push it to the window. Saving alone changed nothing on screen -
            // ApplyBackgroundImage is only reached on pick/remove/stretch, so
            // the slider looked dead until the user touched one of those.
            // This is the opacity-only path, so no re-decode and no flicker.
            (App.MainWindow as MainWindow)?.ApplyBackgroundImageOpacity(newValue);
            BgOpacityText.Text = $"{newValue}%";
        }

        private void BgStretch_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            // Programmatic restores (page load, Remove) also raise this: only a
            // live user pick may rewrite the store, or init would save a blank
            // path over the user's image.
            if (!_bgReady) return;
            var item = BgStretch.SelectedItem as ComboBoxItem;
            if (item == null) return;
            string? tag = item.Tag as string;
            if (string.IsNullOrWhiteSpace(tag)) return;

            BackgroundImageService.Save(_currentPath, (byte)BgOpacity.Value, tag);
            _savedStretch = tag;
            (App.MainWindow as MainWindow)?.ApplyBackgroundImage();
        }

        private async void SettingsPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // From here on, any Appearance selection is the user's.
            _appearanceReady = true;

            // Restore the saved font choice, then let later changes through.
            fontMode.SelectedIndex = Services.FontPreference.Selected switch
            {
                Services.FontPreference.JimTag => 1,
                Services.FontPreference.UncialTag => 2,
                Services.FontPreference.CormorantTag => 3,
                _ => 0,
            };
            ApplyFontPreviews();
            _fontsReady = true;

            LoadTintIntoUi();

            LoadBackgroundImageIntoUi();

            _syncingStartupToggle = true;
            try
            {
                startupToggle.IsOn = await _startup.IsEnabledAsync();
            }
            catch
            {
                startupToggle.IsOn = false;
            }
            finally
            {
                _syncingStartupToggle = false;
            }

            // Update check: fire-and-forget, non-blocking.
            _ = Vm.CheckForUpdateCommand.ExecuteAsync(null);

        }

        private void UpdateNow_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
            => _ = Vm.UpdateNowCommand.ExecuteAsync(null);

        /// <summary>
        /// Saves the picked face and swaps it in everywhere right now: the
        /// resource keys and text styles are rewritten, then the loaded visual
        /// tree is restamped (see FontPreference).
        /// </summary>
        private void FontMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_fontsReady) return;

            var tag = (fontMode.SelectedItem as ComboBoxItem)?.Tag as string
                      ?? Services.FontPreference.DefaultTag;
            Services.FontPreference.Save(tag);

            var root = (App.MainWindow as MainWindow)?.Content as DependencyObject;
            Services.FontPreference.Apply(root);

            // The stamp carries the old face onto any preview item that was
            // showing it; put every item back to the face it advertises.
            ApplyFontPreviews();
        }

        /// <summary>
        /// Each ComboBoxItem renders its own label in the font it stands for,
        /// so the list doubles as a live preview of the four choices.
        /// </summary>
        private void ApplyFontPreviews()
        {
            foreach (var item in fontMode.Items)
            {
                if (item is ComboBoxItem box && box.Tag is string tag)
                    box.FontFamily = Services.FontPreference.Resolve(tag);
            }
        }

        private async void StartupToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_syncingStartupToggle) return;
            _syncingStartupToggle = true;
            try
            {
                bool ok = await _startup.SetEnabledAsync(startupToggle.IsOn);
                if (!ok)
                {
                    // Revert: request denied (e.g. user dismissed the OS prompt).
                    startupToggle.IsOn = !startupToggle.IsOn;
                }
            }
            catch
            {
                startupToggle.IsOn = !startupToggle.IsOn;
            }
            finally
            {
                _syncingStartupToggle = false;
            }
        }

    }
}
