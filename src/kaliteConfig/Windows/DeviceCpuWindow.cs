// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Threading.Tasks;
using kaliteConfig.Models;
using kaliteConfig.Services;

namespace kaliteConfig.Views;

/// <summary>
/// The Affinity page's "Live CPU" dialog: every device with the exact CPU its
/// interrupts are landing on at that moment - including devices that are not
/// pinned to any core. Data comes from CpuObservationService (kernel ETW
/// DPC/ISR events mapped through the device's function driver), refreshed on a
/// timer so the numbers stay current while the dialog is open.
///
/// Shown as an in-app ContentDialog instead of a separate window, so it lives
/// inside the current page context and shares the app's styling/font.
/// </summary>
public sealed class DeviceCpuWindow
{
    private sealed class Row
    {
        public AffinityDeviceItem Device = null!;
        public TextBlock CpuText = null!;
    }

    private readonly List<Row> _rows = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly ContentDialog _dialog;

    public DeviceCpuWindow(IReadOnlyList<AffinityDeviceItem> devices)
    {
        // AddRef FIRST: it starts the kernel session synchronously, so every
        // IsObserving read below (subtitle, footer, initial row text) reflects
        // the real state. Reading it before AddRef always reported "unavailable"
        // on first open even when the session started fine.
        CpuObservationService.AddRef();

        _dialog = new ContentDialog
        {
            Title = new StackPanel { Spacing = 2 },
            Content = BuildContent(devices),
            CloseButtonText = "Close",
            XamlRoot = null!,
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
        };

        var titlePanel = (StackPanel)_dialog.Title;
        titlePanel.Children.Add(new TextBlock
        {
            Text = "Live CPU - running devices",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = AppFontFamily
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = CpuObservationService.IsObserving
                ? "Live from kernel interrupt events (DPC/ISR). Works for pinned and unpinned devices alike."
                : "Live observation is unavailable (the kernel event session could not be started - see cpu-obs.log), so CPU columns stay blank.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
            Foreground = Brush("TextFillColorSecondaryBrush", "#B5BDAE"),
            FontFamily = AppFontFamily
        });

        _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(750);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        _dialog.Closed += (_, _) =>
        {
            try { _timer.Stop(); } catch { }
            try { CpuObservationService.Release(); } catch { }
        };
    }

    private UIElement BuildContent(IReadOnlyList<AffinityDeviceItem> devices)
    {
        var root = new Grid { Margin = new Thickness(0, 0, 0, 0), RowSpacing = 0 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Column header row
        var headerRow = new Grid { Margin = new Thickness(16, 12, 16, 4), ColumnSpacing = 14, MinWidth = 300 };
        SetupColumns(headerRow);
        Grid.SetRow(headerRow, 0);
        AddCell(headerRow, MakeText("Device", 12, "TextFillColorSecondaryBrush", "#B5BDAE"), 0);
        AddCell(headerRow, MakeText("Category", 12, "TextFillColorSecondaryBrush", "#B5BDAE"), 1);
        AddCell(headerRow, MakeText("Pinned CPUs", 12, "TextFillColorSecondaryBrush", "#B5BDAE"), 2);
        AddCell(headerRow, MakeText("CPU activity", 12, "TextFillColorSecondaryBrush", "#B5BDAE"), 3);
        root.Children.Add(headerRow);

        // Device rows
        var list = new StackPanel { Spacing = 0, Margin = new Thickness(16, 0, 16, 12) };
        foreach (var device in devices)
        {
            var grid = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 2, 0, 2), MinWidth = 300 };
            SetupColumns(grid);

            // Subtle separator between rows
            grid.BorderBrush = Brush("DividerStrokeColorDefaultBrush", "#24FFFFFF");
            grid.BorderThickness = new Thickness(0, 0, 0, 1);

            AddCell(grid, MakeText(device.Name, 13, null, null), 0);
            AddCell(grid, MakeText(device.CategoryLabel, 12, "TextFillColorTertiaryBrush", "#808A7A"), 1);
            AddCell(grid, MakeText(
                string.IsNullOrWhiteSpace(device.AffinityText) ? "System default" : device.AffinityText,
                12, "TextFillColorTertiaryBrush", "#808A7A"), 2);

            var cpu = MakeText(CpuObservationService.DescribeDeviceCpu(device.DeviceInstanceId), 13, "AccentTextFillColorPrimaryBrush", "#7FA6DC");
            AddCell(grid, cpu, 3);
            _rows.Add(new Row { Device = device, CpuText = cpu });

            list.Children.Add(grid);
        }

        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Footer
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 0, 16, 12) };
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);

        foot.Children.Add(new TextBlock
        {
            Text = $"{devices.Count} device{(devices.Count == 1 ? "" : "s")} · updated every 0.75s while this dialog is open.",
            FontSize = 11,
            Foreground = Brush("TextFillColorDisabledBrush", "#545C51"),
            FontFamily = AppFontFamily
        });
        if (CpuObservationService.IsObserving)
        {
            foot.Children.Add(new TextBlock
            {
                Text = "CPU column shows: core # · interrupt count · last seen time. A dash (−) means the device has not fired an interrupt yet.",
                FontSize = 11,
                Foreground = Brush("TextFillColorTertiaryBrush", "#6B7663"),
                FontFamily = AppFontFamily,
                TextWrapping = TextWrapping.Wrap
            });
        }

        return root;
    }

    /// <summary>
    /// Shows the live CPU dialog as an in-app ContentDialog.
    /// </summary>
    public async Task ShowAsync(XamlRoot xamlRoot)
    {
        _dialog.XamlRoot = xamlRoot;
        await _dialog.ShowAsync();
    }

    private void Refresh()
    {
        foreach (var row in _rows)
        {
            row.CpuText.Text = CpuObservationService.DescribeDeviceCpu(row.Device.DeviceInstanceId);
        }
    }

    private static void SetupColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.6, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
    }

    private static void AddCell(Grid grid, FrameworkElement child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static TextBlock MakeText(string text, double size, string? brushKey, string? fallback)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = AppFontFamily
        };
        if (brushKey != null) tb.Foreground = Brush(brushKey, fallback!);
        return tb;
    }

    private static Brush? Brush(string key, string fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush b) return b;
        }
        catch { }
        return null;
    }

    private static string? GetAppFont()
    {
        try
        {
            if (Application.Current.Resources.TryGetValue("KaliteFontFamily", out object? value) && value is string s && !string.IsNullOrEmpty(s))
                return s;
        }
        catch { }
        return null;
    }

    private static FontFamily AppFontFamily =>
        GetAppFont() is string familyName
            ? new FontFamily(familyName)
            : new FontFamily("Segoe UI");
}

