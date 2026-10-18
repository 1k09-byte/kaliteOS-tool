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
    public static class SettingsPageBindings
    {
        public static double UpdateProgressValue(double? percent) => percent ?? 0;
    }
    public sealed partial class SettingsPage : Page
    {
        private readonly kaliteConfig.Services.StartupService _startup = new();
        private bool _syncingStartupToggle;
        private readonly ViewModels.UpdateViewModel Vm = new();

        private static bool _appearanceReady;

        private bool _fontsReady;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;

            backdropMode.SelectionChanged += BackdropMode_SelectionChanged;

            try { SeedTintPicker(); } catch { }
        }

        private void BackdropMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_appearanceReady) BackdropPreference.MaterialChosen = true;
        }

        private (byte R, byte G, byte B) _tintColor = Services.BackdropTint.DefaultColor;
        private bool _tintReady;
        private bool _pickerOpen;

        private void SeedTintPicker()
        {
            var color = Services.BackdropTint.ColorOf(Services.BackdropTint.Load());
            TintPicker.Color = Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);
        }

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

        private void TintFlyout_Opened(object sender, object e)
        {
            SeedTintPicker();
            _pickerOpen = true;
        }

        private void TintFlyout_Closed(object sender, object e)
        {
            _pickerOpen = false;
        }

        private void TintPicker_ColorChanged(object sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs e)
        {
            if (!_tintReady || !_pickerOpen) return;

            _tintColor = (e.NewColor.R, e.NewColor.G, e.NewColor.B);
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

            _tintColor = Services.BackdropTint.DefaultColor;
            SeedTintPicker();

            _tintReady = false;
            try { TintStrength.Value = 0; }
            finally { _tintReady = true; }
            RefreshTintVisuals();
        }

        private bool _bgReady;
        private string _savedStretch = "UniformToFill";
        private string _currentPath = string.Empty;

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
            try
            {
                var path = await PickImageFileAsync();
                if (string.IsNullOrWhiteSpace(path)) return;

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
                System.Diagnostics.Debug.WriteLine($"Background image picker error: {ex.Message}");
                BgImageName.Text = "Error loading image";
            }
        }

        private static nint BgPickerHwnd =>
            App.MainWindow is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0;

        private static async Task<string?> PickImageFileAsync()
        {
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

            (App.MainWindow as MainWindow)?.ApplyBackgroundImageOpacity(newValue);
            BgOpacityText.Text = $"{newValue}%";
        }

        private void BgStretch_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
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
            _appearanceReady = true;

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

            _ = Vm.CheckForUpdateCommand.ExecuteAsync(null);

        }

        private void UpdateNow_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
            => _ = Vm.UpdateNowCommand.ExecuteAsync(null);

        private void FontMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_fontsReady) return;

            var tag = (fontMode.SelectedItem as ComboBoxItem)?.Tag as string
                      ?? Services.FontPreference.DefaultTag;
            Services.FontPreference.Save(tag);

            var root = (App.MainWindow as MainWindow)?.Content as DependencyObject;
            Services.FontPreference.Apply(root);

            ApplyFontPreviews();
        }

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
