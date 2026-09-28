// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using kaliteConfig.Models;
using kaliteConfig.Services;
using kaliteConfig.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;

namespace kaliteConfig.Pages;

public sealed partial class NvidiaSettingsPage : Page
{
    private Dictionary<DisplayInfo, Border> _canvasItems = new();
    private Border? _draggedItem;
    private DisplayInfo? _draggedData;
    private Point _pointerOffset;
    private double _canvasScale = 0.05;
    private Point _desktopOrigin;
    private bool _syncingScaleSelection;

    public NvidiaSettingsViewModel Vm { get; } = new();
    public DisplayViewModel DisplayVm { get; } = new();

    public NvidiaSettingsPage()
    {
        InitializeComponent();
        DisplayVm.Displays.CollectionChanged += OnDisplaysChanged;
        DisplayVm.PropertyChanged += OnVmPropertyChanged;
        ArrangementCanvas.PointerPressed += Canvas_PointerPressed;
        ArrangementCanvas.PointerMoved += Canvas_PointerMoved;
        ArrangementCanvas.PointerReleased += Canvas_PointerReleased;
        ArrangementCanvas.PointerExited += Canvas_PointerReleased;
        ArrangementBounds.SizeChanged += (s, e) => RedrawCanvas();
        Loaded += async (_, _) =>
        {
            DisplayVm.Refresh();
            SyncScaleSelection();
            await Vm.RefreshAsync();
        };
    }

    public static Visibility BoolToVis(bool val) => val ? Visibility.Visible : Visibility.Collapsed;
    private void OnDisplaysChanged(object? sender, NotifyCollectionChangedEventArgs e) => RedrawCanvas();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DisplayVm.SelectedDisplay)) UpdateSelectionVisuals();
        else if (e.PropertyName == nameof(DisplayVm.SelectedScale) || e.PropertyName == nameof(DisplayVm.AvailableScales)) SyncScaleSelection();
    }

    private void SyncScaleSelection()
    {
        int index = DisplayVm.AvailableScales.IndexOf(DisplayVm.SelectedScale);
        if (index < 0) index = DisplayVm.AvailableScales.Count > 0 ? 0 : -1;
        if (ScaleComboBox.SelectedIndex == index) return;
        _syncingScaleSelection = true;
        try { ScaleComboBox.SelectedIndex = index; }
        finally { _syncingScaleSelection = false; }
    }

    private void RedrawCanvas()
    {
        ArrangementCanvas.Children.Clear();
        _canvasItems.Clear();
        if (DisplayVm.Displays.Count == 0 || ArrangementBounds.ActualWidth == 0) return;
        int minX = DisplayVm.Displays.Min(d => d.PositionX), minY = DisplayVm.Displays.Min(d => d.PositionY);
        int maxX = DisplayVm.Displays.Max(d => d.PositionX + d.CurrentWidth), maxY = DisplayVm.Displays.Max(d => d.PositionY + d.CurrentHeight);
        int totalW = maxX - minX, totalH = maxY - minY;
        if (totalW == 0 || totalH == 0) return;
        _canvasScale = Math.Min(ArrangementBounds.ActualWidth * 0.8 / totalW, ArrangementBounds.ActualHeight * 0.8 / totalH);
        if (_canvasScale > 0.1) _canvasScale = 0.1;
        double visualTotalW = totalW * _canvasScale, visualTotalH = totalH * _canvasScale;
        _desktopOrigin = new Point((ArrangementBounds.ActualWidth - visualTotalW) / 2 - minX * _canvasScale,
            (ArrangementBounds.ActualHeight - visualTotalH) / 2 - minY * _canvasScale);
        foreach (var d in DisplayVm.Displays)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(d.IsPrimary ? Colors.SteelBlue : Colors.DimGray),
                BorderBrush = new SolidColorBrush(Colors.LightGray), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Width = d.CurrentWidth * _canvasScale, Height = d.CurrentHeight * _canvasScale,
                Opacity = 0.9, Tag = d,
            };
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = d.DeviceName.Replace(@"\\.\", ""), FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 14 });
            string friendly = string.IsNullOrWhiteSpace(d.FriendlyName) ? d.Manufacturer : d.FriendlyName;
            stack.Children.Add(new TextBlock { Text = friendly, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            if (d.IsPrimary) stack.Children.Add(new TextBlock { Text = "(Primary)", FontSize = 10, Foreground = new SolidColorBrush(Colors.LightSkyBlue), HorizontalAlignment = HorizontalAlignment.Center });
            border.Child = stack;
            var flyout = new MenuFlyout();
            var setPrimary = new MenuFlyoutItem { Text = "Make Primary Display" };
            setPrimary.Click += (s, e) => SetPrimaryDisplay(d);
            flyout.Items.Add(setPrimary);
            border.ContextFlyout = flyout;
            Canvas.SetLeft(border, _desktopOrigin.X + d.PositionX * _canvasScale);
            Canvas.SetTop(border, _desktopOrigin.Y + d.PositionY * _canvasScale);
            _canvasItems[d] = border;
            ArrangementCanvas.Children.Add(border);
        }
        UpdateSelectionVisuals();
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var (display, border) in _canvasItems)
        {
            if (display == DisplayVm.SelectedDisplay)
            {
                border.BorderBrush = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                border.BorderThickness = new Thickness(3); border.Opacity = 1.0;
            }
            else { border.BorderBrush = new SolidColorBrush(Colors.DarkGray); border.BorderThickness = new Thickness(1); border.Opacity = 0.8; }
        }
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        _draggedItem = FindParent<Border>(src);
        if (_draggedItem?.Tag is not DisplayInfo d) return;
        _draggedData = d; DisplayVm.SelectedDisplay = d; _draggedItem.Opacity = 0.5; Canvas.SetZIndex(_draggedItem, 100);
        var pt = e.GetCurrentPoint(ArrangementCanvas).Position;
        _pointerOffset = new Point(pt.X - Canvas.GetLeft(_draggedItem), pt.Y - Canvas.GetTop(_draggedItem));
        ArrangementCanvas.CapturePointer(e.Pointer); e.Handled = true;
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggedItem is null || _draggedData is null) return;
        var pt = e.GetCurrentPoint(ArrangementCanvas).Position;
        double newLeft = pt.X - _pointerOffset.X, newTop = pt.Y - _pointerOffset.Y;
        var otherRects = DisplayVm.Displays.Where(x => x != _draggedData).Select(x => (x.PositionX, x.PositionY, x.CurrentWidth, x.CurrentHeight)).ToList();
        int trueX = (int)((newLeft - _desktopOrigin.X) / _canvasScale), trueY = (int)((newTop - _desktopOrigin.Y) / _canvasScale);
        var snapped = DisplayArrangementService.SnapToGrid((trueX, trueY, _draggedData.CurrentWidth, _draggedData.CurrentHeight), otherRects);
        Canvas.SetLeft(_draggedItem, _desktopOrigin.X + snapped.X * _canvasScale);
        Canvas.SetTop(_draggedItem, _desktopOrigin.Y + snapped.Y * _canvasScale);
        e.Handled = true;
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_draggedItem is null || _draggedData is null) return;
        ArrangementCanvas.ReleasePointerCapture(e.Pointer); _draggedItem.Opacity = 1.0; Canvas.SetZIndex(_draggedItem, 0); UpdateSelectionVisuals();
        int finalX = (int)Math.Round((Canvas.GetLeft(_draggedItem) - _desktopOrigin.X) / _canvasScale);
        int finalY = (int)Math.Round((Canvas.GetTop(_draggedItem) - _desktopOrigin.Y) / _canvasScale);
        int idx = DisplayVm.Displays.IndexOf(_draggedData);
        if (idx >= 0)
        {
            var copy = CloneDisplay(_draggedData, finalX, finalY, _draggedData.IsPrimary);
            DisplayVm.Displays.CollectionChanged -= OnDisplaysChanged;
            DisplayVm.Displays[idx] = copy;
            if (DisplayVm.SelectedDisplay == _draggedData) DisplayVm.SelectedDisplay = copy;
            _draggedItem.Tag = copy; _canvasItems.Remove(_draggedData); _canvasItems[copy] = _draggedItem;
            DisplayVm.Displays.CollectionChanged += OnDisplaysChanged;
        }
        _draggedItem = null; _draggedData = null; e.Handled = true;
    }

    private void ApplyArrangement_Click(object sender, RoutedEventArgs e) => DisplayVm.TryApplyArrangement();

    private void ScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingScaleSelection || sender is not ComboBox box) return;
        int index = box.SelectedIndex;
        if (index < 0 || index >= DisplayVm.AvailableScales.Count) return;
        int value = DisplayVm.AvailableScales[index];
        if (value != DisplayVm.SelectedScale) DisplayVm.SelectedScale = value;
    }

    private void SetPrimaryDisplay(DisplayInfo primary)
    {
        int dx = primary.PositionX, dy = primary.PositionY;
        DisplayVm.Displays.Clear();
        foreach (var d in DisplayVm.Displays.ToArray()) DisplayVm.Displays.Add(CloneDisplay(d, d.PositionX - dx, d.PositionY - dy, d == primary));
        DisplayVm.TryApplyArrangement();
    }

    private static DisplayInfo CloneDisplay(DisplayInfo d, int x, int y, bool primary) => new()
    {
        CcdSourceId = d.CcdSourceId, CcdTargetId = d.CcdTargetId, AdapterId = d.AdapterId, SourceAdapterId = d.SourceAdapterId,
        DeviceName = d.DeviceName, FriendlyName = d.FriendlyName, Manufacturer = d.Manufacturer, ProductCode = d.ProductCode,
        SerialNumber = d.SerialNumber, YearOfManufacture = d.YearOfManufacture, NativeWidth = d.NativeWidth, NativeHeight = d.NativeHeight,
        ExtensionBlockCount = d.ExtensionBlockCount, ConnectionType = d.ConnectionType, RegistryKeyPath = d.RegistryKeyPath,
        IsActive = d.IsActive, IsPrimary = primary, CurrentWidth = d.CurrentWidth, CurrentHeight = d.CurrentHeight,
        CurrentRefreshRate = d.CurrentRefreshRate, CurrentRotationDegrees = d.CurrentRotationDegrees, CurrentScalePercent = d.CurrentScalePercent,
        AvailableScalePercents = d.AvailableScalePercents, IsScaleSupported = d.IsScaleSupported, PositionX = x, PositionY = y,
    };

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parent = VisualTreeHelper.GetParent(child);
        if (parent is null) return null;
        if (parent is T typed) return typed;
        return FindParent<T>(parent);
    }
}
