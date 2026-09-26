// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;

namespace kaliteConfig.Views;

/// <summary>
/// The full viewer: the snip at a size that can actually be inspected, on the same Win2D preview
/// the details panel uses (progressive 512 px -> full resolution, DPI-correct 100%, zoom/pan,
/// region-on-demand for huge captures).
/// </summary>
public sealed partial class SnipViewerWindow : Window
{
    private readonly string _filePath;

    public SnipViewerWindow(string filePath)
    {
        InitializeComponent();

        _filePath = filePath;
        var name = Path.GetFileName(filePath);
        Title = string.IsNullOrEmpty(name) ? "Snip viewer" : name;
        FileNameText.Text = name;
        ToolTipService.SetToolTip(FileNameText, filePath);

        Preview.DoubleTapTogglesZoom = true;
        Preview.ImageLoaded += Preview_ImageLoaded;
        Preview.LoadFailed += Preview_LoadFailed;
        Preview.SourcePath = filePath;

        ResizeToFitContent(0, 0);
    }

    private void Preview_ImageLoaded(object? sender, EventArgs e)
    {
        ImageSizeText.Text = $"{Preview.ImagePixelWidth} × {Preview.ImagePixelHeight} px";
        StatusText.Text = "Full resolution loaded.";
        ResizeToFitContent(Preview.ImagePixelWidth, Preview.ImagePixelHeight);
    }

    private void Preview_LoadFailed(object? sender, string reason)
    {
        StatusText.Text = reason;
        ImageSizeText.Text = string.Empty;
    }

    /// <summary>
    /// Sizes the window so the snip opens comfortably: the image at its DIP size plus the chrome,
    /// clamped to the work area (a 4K capture opens at a size you can actually see).
    /// </summary>
    private void ResizeToFitContent(int pixelWidth, int pixelHeight)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;

            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;

            const int chromeWidth = 60;
            const int chromeHeight = 150;

            int maxWidth = Math.Max(640, (int)(area.Width * 0.92));
            int maxHeight = Math.Max(480, (int)(area.Height * 0.92));

            int wantWidth, wantHeight;
            if (pixelWidth > 0 && pixelHeight > 0)
            {
                wantWidth = (int)Math.Round(pixelWidth / scale) + chromeWidth;
                wantHeight = (int)Math.Round(pixelHeight / scale) + chromeHeight;
            }
            else
            {
                wantWidth = Math.Min(maxWidth, 1100);
                wantHeight = Math.Min(maxHeight, 800);
            }

            wantWidth = Math.Clamp(wantWidth, 520, maxWidth);
            wantHeight = Math.Clamp(wantHeight, 400, maxHeight);

            appWindow.Resize(new SizeInt32(wantWidth, wantHeight));

            var x = area.X + Math.Max(0, (area.Width - wantWidth) / 2);
            var y = area.Y + Math.Max(0, (area.Height - wantHeight) / 2);
            appWindow.Move(new PointInt32(x, y));
        }
        catch { /* sizing is a nicety: never block the viewer */ }
    }

    // ---- toolbar ----------------------------------------------------------

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_filePath);
            var package = new DataPackage();
            package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            Clipboard.SetContent(package);
            StatusText.Text = "Copied to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Copy failed: " + ex.Message;
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ((App)Application.Current).Sniper.OpenInEditor(_filePath);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Edit failed: " + ex.Message;
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                Arguments = $"/select,\"{_filePath}\"",
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not open the folder: " + ex.Message;
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ((App)Application.Current).Sniper.PinImageFile(_filePath);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Pin failed: " + ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- keyboard ---------------------------------------------------------

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private void Fit_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Preview.ZoomToFit();
    }

    private void ActualSize_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Preview.ZoomToActualSize();
    }

    private void ZoomIn_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Preview.ZoomBy(1.25);
    }

    private void ZoomOut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Preview.ZoomBy(1 / 1.25);
    }
}
