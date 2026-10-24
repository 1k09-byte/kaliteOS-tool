using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using kaliteConfig.ViewModels;

namespace kaliteConfig.Pages
{
    public sealed partial class InputPage : Page
    {
        public InputViewModel ViewModel { get; }

        private readonly Dictionary<Input.CapabilityKind, StackPanel> _panels = new();

        public InputPage()
        {
            this.InitializeComponent();
            this.ViewModel = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<InputViewModel>(App.Services) ?? new InputViewModel(
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMouseDiscoveryService>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMouseCapabilityProvider>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IWindowsMouseSettingsService>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMouseTestService>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMouseLog>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMousePrefStore>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.EndgameOp1Protocol>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMouseWriteTransaction>(App.Services));
            this.DataContext = ViewModel;
            _panels[Input.CapabilityKind.DeviceInfo] = InfoPanel;
            _panels[Input.CapabilityKind.WindowsSettings] = WindowsPanel;
            _panels[Input.CapabilityKind.Sensor] = VendorPanel;
            _panels[Input.CapabilityKind.Buttons] = VendorPanel;
            _panels[Input.CapabilityKind.Scroll] = VendorPanel;
            _panels[Input.CapabilityKind.Lighting] = VendorPanel;
            _panels[Input.CapabilityKind.Power] = VendorPanel;
            _panels[Input.CapabilityKind.Profiles] = VendorPanel;
            _panels[Input.CapabilityKind.ReportRate] = ReportRatePanel;
            _panels[Input.CapabilityKind.Test] = TestPanel;
            _panels[Input.CapabilityKind.Log] = LogPanel;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.RoadUpdate += OnRoadUpdate;
            this.Loaded += (_, _) => { ViewModel.Open(); };
            this.Unloaded += (_, _) => { try { ViewModel.Close(); } catch { } };
        }

        private double _roadOffset;
        private double _roadTime;
        private readonly List<Rectangle> _roadDashes = new();
        private FontIcon? _roadMarker;

        private void SpeedRoad_SizeChanged(object sender, SizeChangedEventArgs e) => BuildRoad();

        private void BuildRoad()
        {
            if (SpeedRoadCanvas == null) return;
            SpeedRoadCanvas.Children.Clear();
            _roadDashes.Clear();
            double width = SpeedRoadCanvas.ActualWidth;
            if (width <= 0) return;
            Brush dashBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            Brush markerBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            const double laneY = 40;
            var lane = new Rectangle { Width = width, Height = 2, Fill = dashBrush, Opacity = 0.4 };
            Canvas.SetLeft(lane, 0);
            Canvas.SetTop(lane, laneY + 11);
            SpeedRoadCanvas.Children.Add(lane);
            for (int i = 0; i < 12; i++)
            {
                var dash = new Rectangle { Width = 26, Height = 4, Fill = dashBrush, RadiusX = 2, RadiusY = 2 };
                Canvas.SetTop(dash, laneY + 10);
                SpeedRoadCanvas.Children.Add(dash);
                _roadDashes.Add(dash);
            }
            _roadMarker = new FontIcon { Glyph = "\uE962", Foreground = markerBrush };
            Canvas.SetTop(_roadMarker, laneY - 14);
            SpeedRoadCanvas.Children.Add(_roadMarker);
            _roadOffset = 0;
            LayoutRoad(width);
        }

        private void LayoutRoad(double width)
        {
            if (_roadDashes.Count == 0) return;
            double gap = width / _roadDashes.Count;
            for (int i = 0; i < _roadDashes.Count; i++)
            {
                double x = (i * gap - _roadOffset) % width;
                if (x < 0) x += width;
                Canvas.SetLeft(_roadDashes[i], x - 26);
            }
            if (_roadMarker != null)
            {
                Canvas.SetLeft(_roadMarker, width / 2 - 12);
                Canvas.SetTop(_roadMarker, 26 - 14 + Math.Sin(_roadTime) * 4);
            }
        }

        private void OnRoadUpdate(double countsPerSecond)
        {
            if (SpeedRoadCanvas == null || SpeedRoadCanvas.ActualWidth <= 0) return;
            double clamped = Math.Min(Math.Max(countsPerSecond, 0), 12000);
            _roadOffset = (_roadOffset + clamped * 0.01) % SpeedRoadCanvas.ActualWidth;
            _roadTime += 0.6;
            LayoutRoad(SpeedRoadCanvas.ActualWidth);
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InputViewModel.Capabilities))
                RebuildSections();
        }

        private void RebuildSections()
        {
            object? selected = SectionBar.SelectedItem;
            SectionBar.Items.Clear();
            foreach (var cap in ViewModel.Capabilities)
            {
                var item = new SelectorBarItem { Text = cap.Title, Tag = cap };
                SectionBar.Items.Add(item);
                if (selected is SelectorBarItem old && old.Tag is Input.MouseCapability oldCap && oldCap.Kind == cap.Kind)
                    selected = item;
            }
            if (SectionBar.Items.Count == 0) return;
            if (selected is SelectorBarItem keep && SectionBar.Items.Contains(keep))
                SectionBar.SelectedItem = keep;
            else
                SectionBar.SelectedItem = SectionBar.Items[0];
        }

        private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            foreach (var panel in _panels.Values.Distinct()) panel.Visibility = Visibility.Collapsed;
            var current = ViewModel.SelectedCapability;
            if (sender.SelectedItem is SelectorBarItem item && item.Tag is Input.MouseCapability cap)
            {
                ViewModel.SelectedCapability = cap;
                if (_panels.TryGetValue(cap.Kind, out var panel))
                {
                    if (cap.Kind is Input.CapabilityKind.Sensor or Input.CapabilityKind.Buttons or Input.CapabilityKind.Scroll
                        or Input.CapabilityKind.Lighting or Input.CapabilityKind.Power or Input.CapabilityKind.Profiles)
                        VendorStateCard.Description = cap.Title + ": " + cap.StateText + ". " + cap.Detail;
                    panel.Visibility = Visibility.Visible;
                    if (cap.Kind == Input.CapabilityKind.Test)
                        DispatcherQueue.TryEnqueue(BuildRoad);
                }
            }
            else if (current != null && _panels.TryGetValue(current.Kind, out var keep))
            {
                keep.Visibility = Visibility.Visible;
            }
        }

        private void DeviceGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MouseDeviceRow row) ViewModel.SelectedDevice = row;
        }

        private void ShowIgnored_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is MouseDeviceRow row) ViewModel.ShowIgnoredAnyway(row);
        }

        private void NicknameSave_Click(object sender, RoutedEventArgs e) => ViewModel.SaveNickname();

        private async void WindowsApply_Click(object sender, RoutedEventArgs e) => await ViewModel.ApplyWindowsAsync();

        private async void WindowsReset_Click(object sender, RoutedEventArgs e) => await ViewModel.ResetWindowsSectionAsync();

        private async void WindowsRevert_Click(object sender, RoutedEventArgs e) => await ViewModel.RevertWindowsAsync();

        private async void DeviceApply_Click(object sender, RoutedEventArgs e) => await ViewModel.ApplyDeviceAsync();

        private void TestStart_Click(object sender, RoutedEventArgs e) => ViewModel.StartTest();

        private void TestStop_Click(object sender, RoutedEventArgs e) => ViewModel.StopTest();

        private void DpiAnalyze_Click(object sender, RoutedEventArgs e) => ViewModel.AnalyzeDpi();

        private void ClickTestArea_PointerPressed(object sender, PointerRoutedEventArgs e) => ViewModel.RegisterClick();
    }
}
