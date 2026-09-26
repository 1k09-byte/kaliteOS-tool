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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using WinRT.Interop;
using kaliteConfig.Services;
using kaliteConfig.ViewModels;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.Pages;

public sealed partial class NetworkPage : Page
{
    public NetworkViewModel ViewModel => (NetworkViewModel)DataContext;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statsTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _debounce;

    public NetworkPage()
    {
        InitializeComponent();
        Loaded += NetworkPage_Loaded;
        Unloaded += NetworkPage_Unloaded;
        ViewModel.BannerRequested += OnBanner;
        ViewModel.StatsUpdated += OnStatsUpdated;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetChanged;
    }

    public static Brush DotBrush(bool isUp) => new SolidColorBrush(isUp ? Colors.LimeGreen : Colors.Gray);

    private void NetworkCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Border needs Border's own dependency properties: writing
        // Panel.Background on a Border compiles and never paints.
        if (sender is Border border)
        {
            border.Background = ThemeBrush("SubtleFillColorSecondaryBrush");
            border.BorderBrush = ThemeBrush("AccentFillColorDefaultBrush");
        }
        else if (sender is Panel panel)
        {
            panel.Background = ThemeBrush("SubtleFillColorSecondaryBrush");
        }
    }

    private void NetworkCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = ThemeBrush("LayerFillColorAltBrush");
            border.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }
        else if (sender is Panel panel)
        {
            panel.Background = null;
        }
    }

    private static Brush ThemeBrush(string key)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }
        catch { }
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public static Brush SeverityBrush(NetFindingSeverity s) => new SolidColorBrush(
        s == NetFindingSeverity.Warning ? Colors.OrangeRed : Colors.DodgerBlue);

    private async void NetworkPage_Loaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.RefreshAsync().ConfigureAwait(true); }
        catch (Exception ex) { OnBanner("Load failed", ex.Message); }
    }

    private void NetworkPage_Unloaded(object sender, RoutedEventArgs e)
    {
        try { _statsTimer?.Stop(); } catch { }
        try { _debounce?.Stop(); } catch { }
        ViewModel.BannerRequested -= OnBanner;
        ViewModel.StatsUpdated -= OnStatsUpdated;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetChanged;
    }

    private void OnBanner(string title, string content)
    {
        NetInfoBar.Title = title;
        NetInfoBar.Message = content;
        NetInfoBar.Severity = (title.Contains("fail") || title.Contains("Fail")) ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        NetInfoBar.IsOpen = true;
        UndoInfoButton.Visibility = Visibility.Collapsed;
    }

    private void OnNetChanged(object? sender, EventArgs e)
    {
        try
        {
            var q = DispatcherQueue;
            if (q is null) return;
            _debounce ??= q.CreateTimer();
            _debounce.Stop();
            try { _debounce.Tick -= DebounceTick; } catch { }
            _debounce.Tick += DebounceTick;
            _debounce.Interval = TimeSpan.FromSeconds(1.5);
            _debounce.Start();
        }
        catch { }
    }

    private async void DebounceTick(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
    {
        try
        {
            timer.Stop();
            // Refresh keeps the selected adapter (by GUID) and never clears editors.
            if (Visibility != Visibility.Visible) return;
            try { await ViewModel.RefreshAsync().ConfigureAwait(true); }
            catch (Exception ex) { OnBanner("Live refresh failed", ex.Message); }
        }
        catch { }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.RefreshAsync().ConfigureAwait(true); }
        catch (Exception ex) { OnBanner("Refresh failed", ex.Message); }
    }

    private async void AdapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterList.SelectedItem is NetAdapterInfo a)
        {
            try { await ViewModel.SelectAdapterAsync(a).ConfigureAwait(true); }
            catch (Exception ex) { OnBanner("Select failed", ex.Message); }
        }
    }

    private void SensitiveValue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NetLabelValue row)
            row.ToggleReveal();
    }

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NetLabelValue row)
        {
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText($"{row.Label}: {row.Value}");
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
            catch (Exception ex) { OnBanner("Copy failed", ex.Message); }
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAdapter is null) return;
        if (ViewModel.IsRiskyTarget && !ViewModel.ConfirmRiskyEdit)
        {
            OnBanner("Confirm first", "Virtual, VPN or disabled adapters need the explicit confirmation checkbox before anything writes.");
            return;
        }
        if (!ViewModel.ConfirmRiskyEdit && ViewModel.Adapters.Count(a => a.IsUp && (a.Kind == NetAdapterKind.Ethernet || a.Kind == NetAdapterKind.WiFi)) <= 1
            && ViewModel.SelectedAdapter.IsUp)
        {
            OnBanner("Confirm first", "This looks like the only adapter with a link. Tick the confirmation checkbox to apply anyway - the keep-countdown still protects you.");
            return;
        }
        try { await ViewModel.ApplyAsync().ConfigureAwait(true); }
        catch (Exception ex) { OnBanner("Apply failed", ex.Message); }
    }

    private void Keep_Click(object sender, RoutedEventArgs e) => ViewModel.KeepChanges();

    private async void RevertNow_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.RevertAsync().ConfigureAwait(true); }
        catch (Exception ex) { OnBanner("Revert failed", ex.Message); }
    }

    private async void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ResetDefaultsAsync().ConfigureAwait(true); }
        catch (Exception ex) { OnBanner("Reset failed", ex.Message); }
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.RollbackSnapshotAsync(SnapshotPicker.SelectedValue as string).ConfigureAwait(true); }
        catch (Exception ex) { OnBanner("Rollback failed", ex.Message); }
    }

    private void StageFix_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NetDoctorFinding f)
            ViewModel.StageFixCommand.Execute(f);
    }

    private void UndoInfoBar_Click(object sender, RoutedEventArgs e) { }

    private void DeviceManager_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("mmc.exe", "devmgmt.msc")
            {
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { OnBanner("Could not open Device Manager", ex.Message); }
    }

    private void CopyDriver_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(string.Join("\n", ViewModel.DriverRows.Select(r => $"{r.Label}: {r.Value}")));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            OnBanner("Copied", "Driver info copied as text.");
        }
        catch (Exception ex) { OnBanner("Copy failed", ex.Message); }
    }

    private void NetTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            bool stats = ReferenceEquals(NetTabs.SelectedItem, StatsTab);
            if (stats && _statsTimer is null)
            {
                _statsTimer = DispatcherQueue.CreateTimer();
                _statsTimer.Interval = TimeSpan.FromSeconds(1);
                _statsTimer.Tick += async (_, _) =>
                {
                    try { await ViewModel.TickStatsAsync().ConfigureAwait(true); }
                    catch { }
                };
            }
            if (_statsTimer is not null)
            {
                if (stats) _statsTimer.Start(); else _statsTimer.Stop();
            }
        }
        catch { }
    }

    private void OnStatsUpdated()
    {
        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                DrawStats();
                var a = ViewModel.SelectedAdapter;
                StatsLine1.Text = a is null ? "" : $"{a.Name}: see graph (Mbps)";
                UpdateCounters(a?.Guid ?? "");
            });
        }
        catch { }
    }

    private async void UpdateCounters(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return;
        try
        {
            var c = await NetAdapterService.GetCountersAsync(guid).ConfigureAwait(true);
            StatsLine2.Text = $"packets in {c.InUcast + c.InNUcast} / out {c.OutUcast + c.OutNUcast} · " +
                $"errors in {c.InErr} / out {c.OutErr} · discards in {c.InDisc} / out {c.OutDisc} · " +
                $"multicast/broadcast in {c.InNUcast} / out {c.OutNUcast}";
        }
        catch { }
    }

    private void DrawStats()
    {
        try
        {
            var series = ViewModel.Series;
            StatsCanvas.Children.Clear();
            double w = StatsCanvas.ActualWidth;
            double h = StatsCanvas.ActualHeight;
            if (w <= 0 || h <= 0 || series.Count < 2) return;
            var rx = new List<double>();
            var tx = new List<double>();
            for (int i = 1; i < series.Count; i++)
            {
                double dt = (series[i].AtUtc - series[i - 1].AtUtc).TotalSeconds;
                if (dt <= 0) continue;
                rx.Add(8.0 * (series[i].RxBytes - series[i - 1].RxBytes) / dt / 1e6);
                tx.Add(8.0 * (series[i].TxBytes - series[i - 1].TxBytes) / dt / 1e6);
            }
            if (rx.Count == 0) return;
            double max = Math.Max(1, ViewModel.SeriesMaxMbps);
            var (step, first) = GraphTicks.NiceTicks(0, max, 4);
            var gridBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 128, 128, 128));
            for (double v = first; v <= max + 1e-9; v += step)
            {
                double y = h - 18 - (v / max) * (h - 30);
                StatsCanvas.Children.Add(new Line
                {
                    X1 = 34, X2 = w - 6, Y1 = y, Y2 = y,
                    Stroke = gridBrush, StrokeThickness = 1,
                });
                StatsCanvas.Children.Add(new TextBlock
                {
                    Text = GraphTicks.Label(v), FontSize = 10,
                    Foreground = gridBrush,
                });
                var lbl = (TextBlock)StatsCanvas.Children[StatsCanvas.Children.Count - 1];
                Canvas.SetLeft(lbl, 2);
                Canvas.SetTop(lbl, y - 8);
            }
            // Min/max-per-bucket decimation so spikes survive.
            int buckets = Math.Max(1, (int)(w - 40));
            var rxBands = NetParsing.DecimateMinMax(rx, buckets);
            var txBands = NetParsing.DecimateMinMax(tx, buckets);
            var rxPoly = new Polyline { Stroke = new SolidColorBrush(Colors.DodgerBlue), StrokeThickness = 2 };
            var txPoly = new Polyline { Stroke = new SolidColorBrush(Colors.Orange), StrokeThickness = 2 };
            for (int i = 0; i < rxBands.Count; i++)
            {
                double x = 34 + (i / (double)Math.Max(1, rxBands.Count - 1)) * (w - 40);
                rxPoly.Points.Add(new Windows.Foundation.Point(x, h - 18 - Math.Min(1, rxBands[i].Max / max) * (h - 30)));
                txPoly.Points.Add(new Windows.Foundation.Point(x, h - 18 - Math.Min(1, txBands[i].Max / max) * (h - 30)));
            }
            StatsCanvas.Children.Add(rxPoly);
            StatsCanvas.Children.Add(txPoly);
            double lastRx = rx[^1], lastTx = tx[^1];
            StatsLine1.Text = $"down {lastRx:0.#} Mbps · up {lastTx:0.#} Mbps (scale {max:0.#})";
        }
        catch { }
    }

    private async void SpeedStart_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.StartSpeedTestAsync().ConfigureAwait(true); }
        catch (Exception ex) { OnBanner("Speed test failed", ex.Message); }
    }

    private void SpeedCancel_Click(object sender, RoutedEventArgs e) => ViewModel.CancelSpeedTest();

    private void SysChangedOnly_Changed(object sender, RoutedEventArgs e)
    {
        ViewModel.ApplySysFilter(SysChangedOnlyToggle.IsChecked == true);
    }

    private async void SysReRead_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ReReadSysAsync().ConfigureAwait(true); }
        catch (Exception ex) { OnBanner("Re-read failed", ex.Message); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var fmt = (ExportFormatBox.SelectedItem as ComboBoxItem)?.Content as string ?? "JSON";
            bool all = ExportAllBox.IsChecked == true;
            var targets = all ? ViewModel.Adapters.ToList()
                : (ViewModel.SelectedAdapter is { } s ? new List<NetAdapterInfo> { s } : new List<NetAdapterInfo>());
            if (targets.Count == 0) { OnBanner("Nothing to export", "Pick an adapter first."); return; }
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            string ext = fmt == "CSV" ? ".csv" : fmt == "Text" ? ".txt" : ".json";
            picker.FileTypeChoices.Add(fmt + " report", new[] { ext });
            picker.SuggestedFileName = all ? $"network-all-{DateTime.Now:yyyyMMdd_HHmmss}{ext}"
                : $"{targets[0].Name}-snip-{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            using var out_ = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
            if (fmt == "JSON" && targets.Count > 1)
            {
                var parts = new List<string>();
                foreach (var a in targets) parts.Add(await JsonForAsync(a));
                using var w = new StreamWriter(out_.AsStreamForWrite(), System.Text.Encoding.UTF8, 1024, leaveOpen: true);
                await w.WriteAsync("[\n" + string.Join(",\n", parts) + "\n]\n");
                await w.FlushAsync();
            }
            else
            {
                using var w = new StreamWriter(out_.AsStreamForWrite(), System.Text.Encoding.UTF8, 1024, leaveOpen: true);
                foreach (var a in targets)
                {
                    var text = fmt == "CSV" ? await CsvForAsync(a) : fmt == "Text" ? await ViewModel.ExportTextAsync(a) : await JsonForAsync(a);
                    await w.WriteAsync(text + "\n");
                    await w.FlushAsync();
                }
            }
            OnBanner("Exported", $"Wrote {targets.Count} adapter(s) to {file.Name}.");
        }
        catch (Exception ex) { OnBanner("Export failed", ex.Message); }
    }

    private async Task<string> JsonForAsync(NetAdapterInfo a)
    {
        var props = await NetAdapterService.GetAdvancedAsync(a.RegistryKey).ConfigureAwait(true);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Adapter = a.Name,
            Description = a.Description,
            ExportedUtc = DateTime.UtcNow,
            Values = props.ToDictionary(p => p.Keyword, p => p.Current),
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> CsvForAsync(NetAdapterInfo a)
    {
        var props = await NetAdapterService.GetAdvancedAsync(a.RegistryKey).ConfigureAwait(true);
        var sb = new System.Text.StringBuilder("Keyword,DisplayName,Current,Default,Changed\n");
        foreach (var p in props)
            sb.AppendLine($"\"{p.Keyword}\",\"{p.DisplayName}\",\"{p.Current}\",\"{p.Default}\",{p.Changed}");
        return sb.ToString();
    }

    private async void CopyText_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var a = ViewModel.SelectedAdapter;
            if (a is null) { OnBanner("Nothing to copy", "Pick an adapter first."); return; }
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(await ViewModel.ExportTextAsync(a).ConfigureAwait(true));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            OnBanner("Copied", "Adapter report copied as text.");
        }
        catch (Exception ex) { OnBanner("Copy failed", ex.Message); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await ViewModel.ImportProfileAsync(file.Path).ConfigureAwait(true);
        }
        catch (Exception ex) { OnBanner("Import failed", ex.Message); }
    }

    private void StageImport_Click(object sender, RoutedEventArgs e)
    {
        try { ViewModel.StageImportCommand.Execute(null); }
        catch (Exception ex) { OnBanner("Stage failed", ex.Message); }
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.CompareAsync().ConfigureAwait(true); }
        catch (Exception ex) { OnBanner("Compare failed", ex.Message); }
    }
}


