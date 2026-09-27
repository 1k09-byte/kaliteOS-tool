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
using Microsoft.UI.Xaml.Shapes;
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
    
    // Drag state
    private Border? _draggedItem;
    private DisplayInfo? _draggedData;
    private Point _pointerOffset;
    
    // Scale factor between true desktop resolution and the visual canvas
    private double _canvasScale = 0.05; 
    private Point _desktopOrigin;

    /// <summary>Guards the one-way sync into the scale dropdown from echoing back as a pick.</summary>
    private bool _syncingScaleSelection;
    
    /// <summary>Driver-owned picture controls for the selected display.</summary>
    public NvidiaSettingsViewModel Vm { get; } = new();

    /// <summary>Display management: arrangement, resolution, refresh rate, orientation.</summary>
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

        this.Loaded += async (_, _) =>
        {
            // Display management first (the arrangement canvas draws from it), then the
            // driver-owned picture controls.
            DisplayVm.Refresh();
            // The scale dropdown has no SelectedItem binding to fall back on, so it is
            // given its position once the list exists.
            SyncScaleSelection();
            await Vm.RefreshAsync();
        };
    }

    public static Visibility BoolToVis(bool val) => val ? Visibility.Visible : Visibility.Collapsed;

    private void OnDisplaysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RedrawCanvas();
    }
    
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DisplayVm.SelectedDisplay))
        {
            UpdateSelectionVisuals();
        }
        else if (e.PropertyName == nameof(DisplayVm.SelectedScale) ||
                 e.PropertyName == nameof(DisplayVm.AvailableScales))
        {
            SyncScaleSelection();
        }
    }

    /// <summary>
    /// Mirrors the view model's scale onto the dropdown by position.
    ///
    /// The list is re-filled whenever the selected display changes, which drops the
    /// dropdown's own selection, so it has to be re-applied afterwards or the box is left
    /// blank. Going through the index keeps this working for a list of plain numbers.
    /// </summary>
    private void SyncScaleSelection()
    {
        int index = DisplayVm.AvailableScales.IndexOf(DisplayVm.SelectedScale);
        if (index < 0)
        {
            // The current value is not one the panel offers. Selecting the nearest offered
            // step is honest, where blank is not.
            index = DisplayVm.AvailableScales.Count > 0 ? 0 : -1;
        }

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

        // 1. Find bounding box of all displays to compute scale so they fit
        int minX = DisplayVm.Displays.Min(d => d.PositionX);
        int minY = DisplayVm.Displays.Min(d => d.PositionY);
        int maxX = DisplayVm.Displays.Max(d => d.PositionX + d.CurrentWidth);
        int maxY = DisplayVm.Displays.Max(d => d.PositionY + d.CurrentHeight);

        int totalW = maxX - minX;
        int totalH = maxY - minY;

        // Leave a 20% margin
        double availW = ArrangementBounds.ActualWidth * 0.8;
        double availH = ArrangementBounds.ActualHeight * 0.8;

        if (totalW == 0 || totalH == 0) return;

        double scaleX = availW / totalW;
        double scaleY = availH / totalH;
        _canvasScale = Math.Min(scaleX, scaleY);
        if (_canvasScale > 0.1) _canvasScale = 0.1; // Cap size

        // Center on canvas
        double visualTotalW = totalW * _canvasScale;
        double visualTotalH = totalH * _canvasScale;
        
        double offsetX = (ArrangementBounds.ActualWidth - visualTotalW) / 2.0;
        double offsetY = (ArrangementBounds.ActualHeight - visualTotalH) / 2.0;
        
        _desktopOrigin = new Point(offsetX - (minX * _canvasScale), offsetY - (minY * _canvasScale));

        // 2. Draw each display
        foreach (var d in DisplayVm.Displays)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(d.IsPrimary ? Colors.SteelBlue : Colors.DimGray),
                BorderBrush = new SolidColorBrush(Colors.LightGray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Width = d.CurrentWidth * _canvasScale,
                Height = d.CurrentHeight * _canvasScale,
                Opacity = 0.9,
                Tag = d,
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            // Name: \.\DISPLAY1
            stack.Children.Add(new TextBlock 
            { 
                Text = d.DeviceName.Replace(@"\\.\", ""),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 14
            });

            // Make + Model
            string friendly = string.IsNullOrWhiteSpace(d.FriendlyName) ? d.Manufacturer : d.FriendlyName;
            stack.Children.Add(new TextBlock 
            { 
                Text = friendly, 
                FontSize = 10, 
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            });

            if (d.IsPrimary)
            {
                stack.Children.Add(new TextBlock 
                { 
                    Text = "(Primary)", 
                    FontSize = 10, 
                    Foreground = new SolidColorBrush(Colors.LightSkyBlue),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            border.Child = stack;

            // Optional ContextFlyout to set primary display
            var flyout = new MenuFlyout();
            var setPrimaryItem = new MenuFlyoutItem { Text = "Make Primary Display" };
            setPrimaryItem.Click += (s, e) => 
            {
                SetPrimaryDisplay(d);
            };
            flyout.Items.Add(setPrimaryItem);
            border.ContextFlyout = flyout;

            Canvas.SetLeft(border, _desktopOrigin.X + (d.PositionX * _canvasScale));
            Canvas.SetTop(border, _desktopOrigin.Y + (d.PositionY * _canvasScale));

            _canvasItems[d] = border;
            ArrangementCanvas.Children.Add(border);
        }

        UpdateSelectionVisuals();
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var kvp in _canvasItems)
        {
            var d = kvp.Key;
            var border = kvp.Value;
            
            if (d == DisplayVm.SelectedDisplay)
            {
                border.BorderBrush = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                border.BorderThickness = new Thickness(3);
                border.Opacity = 1.0;
            }
            else
            {
                border.BorderBrush = new SolidColorBrush(Colors.DarkGray);
                border.BorderThickness = new Thickness(1);
                border.Opacity = 0.8;
            }
        }
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src)
        {
            _draggedItem = FindParent<Border>(src);
            if (_draggedItem != null && _draggedItem.Tag is DisplayInfo d)
            {
                _draggedData = d;
                // Auto-select when clicked
                DisplayVm.SelectedDisplay = d;

                _draggedItem.Opacity = 0.5;
                Canvas.SetZIndex(_draggedItem, 100);

                var pt = e.GetCurrentPoint(ArrangementCanvas).Position;
                double left = Canvas.GetLeft(_draggedItem);
                double top = Canvas.GetTop(_draggedItem);
                _pointerOffset = new Point(pt.X - left, pt.Y - top);

                ArrangementCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggedItem != null && _draggedData != null)
        {
            var pt = e.GetCurrentPoint(ArrangementCanvas).Position;
            double newLeft = pt.X - _pointerOffset.X;
            double newTop = pt.Y - _pointerOffset.Y;

            // Calculate true desktop rects for snapping
            var otherRects = DisplayVm.Displays
                .Where(x => x != _draggedData)
                .Select(x => (x.PositionX, x.PositionY, x.CurrentWidth, x.CurrentHeight))
                .ToList();

            int trueX = (int)((newLeft - _desktopOrigin.X) / _canvasScale);
            int trueY = (int)((newTop - _desktopOrigin.Y) / _canvasScale);

            var snapped = DisplayArrangementService.SnapToGrid((trueX, trueY, _draggedData.CurrentWidth, _draggedData.CurrentHeight), otherRects);

            // Back to visual
            newLeft = _desktopOrigin.X + (snapped.X * _canvasScale);
            newTop = _desktopOrigin.Y + (snapped.Y * _canvasScale);

            Canvas.SetLeft(_draggedItem, newLeft);
            Canvas.SetTop(_draggedItem, newTop);

            e.Handled = true;
        }
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_draggedItem != null && _draggedData != null)
        {
            ArrangementCanvas.ReleasePointerCapture(e.Pointer);
            _draggedItem.Opacity = 1.0;
            Canvas.SetZIndex(_draggedItem, 0);
            UpdateSelectionVisuals();

            // Commit back to DisplayInfo via reflection since Position is init-only; 
            // In a real app we'd mutate it or re-create it. We can mutate it via reflection 
            // since DisplayInfo properties are { get; init; } but backed by fields.
            // Actually, safe way: create a clone modifying X/Y in the ViewModel's list.
            
            double left = Canvas.GetLeft(_draggedItem);
            double top = Canvas.GetTop(_draggedItem);
            
            int finalX = (int)Math.Round((left - _desktopOrigin.X) / _canvasScale);
            int finalY = (int)Math.Round((top - _desktopOrigin.Y) / _canvasScale);

            // Swap out the DisplayInfo object in the collection to update the position
            int idx = DisplayVm.Displays.IndexOf(_draggedData);
            if (idx >= 0)
            {
                var copy = new DisplayInfo
                {
                    CcdSourceId = _draggedData.CcdSourceId,
                    CcdTargetId = _draggedData.CcdTargetId,
                    AdapterId = _draggedData.AdapterId,
                    SourceAdapterId = _draggedData.SourceAdapterId,
                    DeviceName = _draggedData.DeviceName,
                    FriendlyName = _draggedData.FriendlyName,
                    Manufacturer = _draggedData.Manufacturer,
                    ProductCode = _draggedData.ProductCode,
                    SerialNumber = _draggedData.SerialNumber,
                    YearOfManufacture = _draggedData.YearOfManufacture,
                    NativeWidth = _draggedData.NativeWidth,
                    NativeHeight = _draggedData.NativeHeight,
                    ExtensionBlockCount = _draggedData.ExtensionBlockCount,
                    ConnectionType = _draggedData.ConnectionType,
                    RegistryKeyPath = _draggedData.RegistryKeyPath,
                    IsActive = _draggedData.IsActive,
                    IsPrimary = _draggedData.IsPrimary,
                    CurrentWidth = _draggedData.CurrentWidth,
                    CurrentHeight = _draggedData.CurrentHeight,
                    CurrentRefreshRate = _draggedData.CurrentRefreshRate,
                    CurrentRotationDegrees = _draggedData.CurrentRotationDegrees,
                    CurrentScalePercent = _draggedData.CurrentScalePercent,
                    AvailableScalePercents = _draggedData.AvailableScalePercents,
                    IsScaleSupported = _draggedData.IsScaleSupported,
                    PositionX = finalX,
                    PositionY = finalY
                };
                
                // Suppress redraw during silent update
                DisplayVm.Displays.CollectionChanged -= OnDisplaysChanged;
                DisplayVm.Displays[idx] = copy;
                
                if (DisplayVm.SelectedDisplay == _draggedData)
                    DisplayVm.SelectedDisplay = copy;
                    
                _draggedItem.Tag = copy; // update tag for future drags
                _canvasItems.Remove(_draggedData);
                _canvasItems[copy] = _draggedItem;
                
                DisplayVm.Displays.CollectionChanged += OnDisplaysChanged;
            }

            _draggedItem = null;
            _draggedData = null;
            e.Handled = true;
        }
    }

    private void ApplyArrangement_Click(object sender, RoutedEventArgs e)
    {
        DisplayVm.TryApplyArrangement();
    }

    /// <summary>
    /// Feeds a scale picked in the dropdown back into the view model.
    ///
    /// The dropdown can be empty or hold a selection the current display no longer offers,
    /// and it drops its selection in both cases, so the index is bounds-checked here rather
    /// than assumed. Changes made by <see cref="SyncScaleSelection"/> are ignored, since
    /// those are the model talking to the view, not the user choosing something.
    /// </summary>
    private void ScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingScaleSelection) return;
        if (sender is not ComboBox box) return;

        int index = box.SelectedIndex;
        if (index < 0 || index >= DisplayVm.AvailableScales.Count) return;

        int value = DisplayVm.AvailableScales[index];
        if (value != DisplayVm.SelectedScale) DisplayVm.SelectedScale = value;
    }

    private void SetPrimaryDisplay(DisplayInfo newPrimary)
    {
        // This is a UI-only state update. True backend 
        // CCD primary swap is complex (requires shifting everything relative to 0,0)
        // We will do a full refresh to allow Windows to handle the primary designation
        // for now, but UI state represents it.
        var copy = new DisplayInfo
        {
            CcdSourceId = newPrimary.CcdSourceId,
            CcdTargetId = newPrimary.CcdTargetId,
            AdapterId = newPrimary.AdapterId,
            SourceAdapterId = newPrimary.SourceAdapterId,
            DeviceName = newPrimary.DeviceName,
            FriendlyName = newPrimary.FriendlyName,
            Manufacturer = newPrimary.Manufacturer,
            ProductCode = newPrimary.ProductCode,
            SerialNumber = newPrimary.SerialNumber,
            YearOfManufacture = newPrimary.YearOfManufacture,
            NativeWidth = newPrimary.NativeWidth,
            NativeHeight = newPrimary.NativeHeight,
            ExtensionBlockCount = newPrimary.ExtensionBlockCount,
            ConnectionType = newPrimary.ConnectionType,
            RegistryKeyPath = newPrimary.RegistryKeyPath,
            IsActive = newPrimary.IsActive,
            IsPrimary = true, // Set to true
            CurrentWidth = newPrimary.CurrentWidth,
            CurrentHeight = newPrimary.CurrentHeight,
            CurrentRefreshRate = newPrimary.CurrentRefreshRate,
            CurrentRotationDegrees = newPrimary.CurrentRotationDegrees,
            CurrentScalePercent = newPrimary.CurrentScalePercent,
            AvailableScalePercents = newPrimary.AvailableScalePercents,
            IsScaleSupported = newPrimary.IsScaleSupported,
            PositionX = 0, // Primary is always 0,0
            PositionY = 0
        };

        // Shift everything else relative to this new primary
        int dx = newPrimary.PositionX;
        int dy = newPrimary.PositionY;

        var newList = new List<DisplayInfo>();
        foreach (var d in DisplayVm.Displays)
        {
            if (d == newPrimary)
            {
                newList.Add(copy);
            }
            else
            {
                // Shift others
                newList.Add(new DisplayInfo
                {
                    CcdSourceId = d.CcdSourceId,
                    CcdTargetId = d.CcdTargetId,
                    AdapterId = d.AdapterId,
                    SourceAdapterId = d.SourceAdapterId,
                    DeviceName = d.DeviceName,
                    FriendlyName = d.FriendlyName,
                    Manufacturer = d.Manufacturer,
                    ProductCode = d.ProductCode,
                    SerialNumber = d.SerialNumber,
                    YearOfManufacture = d.YearOfManufacture,
                    NativeWidth = d.NativeWidth,
                    NativeHeight = d.NativeHeight,
                    ExtensionBlockCount = d.ExtensionBlockCount,
                    ConnectionType = d.ConnectionType,
                    RegistryKeyPath = d.RegistryKeyPath,
                    IsActive = d.IsActive,
                    IsPrimary = false,
                    CurrentWidth = d.CurrentWidth,
                    CurrentHeight = d.CurrentHeight,
                    CurrentRefreshRate = d.CurrentRefreshRate,
                    CurrentRotationDegrees = d.CurrentRotationDegrees,
                    CurrentScalePercent = d.CurrentScalePercent,
                    AvailableScalePercents = d.AvailableScalePercents,
                    IsScaleSupported = d.IsScaleSupported,
                    PositionX = d.PositionX - dx,
                    PositionY = d.PositionY - dy
                });
            }
        }

        DisplayVm.Displays.Clear();
        foreach (var item in newList) DisplayVm.Displays.Add(item);
        
        DisplayVm.TryApplyArrangement(); // Apply it and trigger countdown
    }



    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }
    
}
