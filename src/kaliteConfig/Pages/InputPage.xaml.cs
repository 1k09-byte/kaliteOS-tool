using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using System;
using System.Collections.Generic;
using kaliteConfig.ViewModels;

namespace kaliteConfig.Pages
{
    public sealed partial class InputPage : Page
    {
        public InputViewModel ViewModel { get; }

        private readonly List<Rectangle> _dashes = new();
        private readonly List<Line> _streaks = new();
        private readonly List<double> _history = new();
        private Polyline? _sparkLine;
        private double _roadOffset;
        private double _roadPhase;
        private double _clock;
        private double _lean;
        private double _tilt;
        private double _speed;
        private double _targetLean;
        private double _targetTilt;
        private double _targetSpeed;
        private double _hoverScale = 1;
        private double _hoverScaleTarget = 1;
        private double _hoverTilt;
        private double _hoverTiltTarget;
        private double _pressLeft;
        private double _pressRight;
        private double _pressWheel;
        private double _pressBack;
        private double _pressForward;
        private double _wantedLeft;
        private double _wantedRight;
        private double _wantedWheel;
        private double _wantedBack;
        private double _wantedForward;
        private long _lastFrame;
        private double _frameAccumulator;
        private double _appliedLean = double.NaN;
        private double _appliedTilt = double.NaN;
        private double _appliedScale = double.NaN;
        private bool _rendering;
        private bool _pressInsideRig;

        public InputPage()
        {
            this.InitializeComponent();
            this.ViewModel = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<InputViewModel>(App.Services) ?? new InputViewModel(
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMouseDiscoveryService>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IWindowsMouseSettingsService>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMouseTestService>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMouseLog>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMousePrefStore>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMouseProtocolRegistry>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IMouseWriteTransaction>(App.Services),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Input.IPollRateAdvisor>(App.Services));
            this.DataContext = ViewModel;
            ViewModel.Motion += OnMotion;
            ViewModel.PressedButtonsChanged += OnPressedButtons;
            this.Loaded += (_, _) =>
            {
                ViewModel.Open();
                BuildRoad();
                BuildSpark();
                StartRendering();
                if (App.MainWindow is { } window) window.Activated += HostWindow_Activated;
            };
            this.Unloaded += (_, _) =>
            {
                if (App.MainWindow is { } window) window.Activated -= HostWindow_Activated;
                StopRendering();
                ViewModel.Motion -= OnMotion;
                ViewModel.PressedButtonsChanged -= OnPressedButtons;
                try { ViewModel.Close(); } catch { }
            };
        }

        private void HostWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                StopRendering();
                return;
            }
            _lastFrame = 0;
            StartRendering();
            try { ViewModel.RefreshDevices(); } catch { }
        }

        private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            string tag = sender.SelectedItem?.Text ?? "Mouse";
            MouseSection.Visibility = tag == "Mouse" ? Visibility.Visible : Visibility.Collapsed;
            ButtonsSection.Visibility = tag == "Buttons" ? Visibility.Visible : Visibility.Collapsed;
            WindowsSection.Visibility = tag == "Windows" ? Visibility.Visible : Visibility.Collapsed;
            TestsSection.Visibility = tag == "Tests" ? Visibility.Visible : Visibility.Collapsed;
            IgnoredSection.Visibility = tag == "Ignored" ? Visibility.Visible : Visibility.Collapsed;
            LogSection.Visibility = tag == "Log" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Rescan_Click(object sender, RoutedEventArgs e) => ViewModel.Rescan();

        private void UseRecommendation_Click(object sender, RoutedEventArgs e) => ViewModel.UseRecommendation();

        private void ShowIgnored_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is MouseDeviceRow row) ViewModel.ShowIgnoredAnyway(row);
        }

        private void NicknameSave_Click(object sender, RoutedEventArgs e) => ViewModel.SaveNickname();

        private async void DeviceApply_Click(object sender, RoutedEventArgs e) => await ViewModel.ApplyDeviceAsync();

        private async void DeviceRevert_Click(object sender, RoutedEventArgs e) => await ViewModel.RevertDeviceAsync();

        private async void WindowsApply_Click(object sender, RoutedEventArgs e) => await ViewModel.ApplyWindowsAsync();

        private async void WindowsReset_Click(object sender, RoutedEventArgs e) => await ViewModel.ResetWindowsSectionAsync();

        private async void WindowsRevert_Click(object sender, RoutedEventArgs e) => await ViewModel.RevertWindowsAsync();

        private void TestStart_Click(object sender, RoutedEventArgs e) => ViewModel.StartTestManually();

        private void TestStop_Click(object sender, RoutedEventArgs e) => ViewModel.StopTest();

        private void DpiAnalyze_Click(object sender, RoutedEventArgs e) => ViewModel.AnalyzeDpi();

        private void ClickTestArea_PointerPressed(object sender, PointerRoutedEventArgs e) => ViewModel.RegisterClick();

        private void StartRendering()
        {
            if (_rendering) return;
            _lastFrame = 0;
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }

        private void StopRendering()
        {
            if (!_rendering) return;
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }

        private void OnRendering(object? sender, object e)
        {
            if (RigTransform is null || RigProjection is null || LeftPress is null || RightPress is null || WheelPress is null || BackPress is null || ForwardPress is null) return;
            if (MouseSection is not null && MouseSection.Visibility != Visibility.Visible) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double dt = _lastFrame == 0 ? 1.0 / 60.0 : (now - _lastFrame) / (double)System.Diagnostics.Stopwatch.Frequency;
            _lastFrame = now;
            if (dt <= 0) return;
            if (dt > 0.1) dt = 1.0 / 60.0;
            _frameAccumulator += dt;
            if (_frameAccumulator < 0.0135) return;
            dt = Math.Min(_frameAccumulator, 0.04);
            _frameAccumulator = 0;
            bool hasMotion = ViewModel.TakeMotion(out int motionX, out int motionY);
            if (hasMotion)
            {
                double distance = Math.Sqrt((double)motionX * motionX + (double)motionY * motionY);
                double frameSpeed = distance / Math.Max(dt, 0.001);
                double gain = 0.18 + Math.Min(frameSpeed, 24000) / 26000;
                _targetLean = Math.Clamp(motionX * gain, -22, 22);
                _targetTilt = Math.Clamp(-motionY * gain * 0.85, -18, 18);
                _targetSpeed = Math.Min(frameSpeed * 0.6, 24000);
            }

            _lean += (_targetLean - _lean) * (1 - Math.Exp(-dt / 0.045));
            _tilt += (_targetTilt - _tilt) * (1 - Math.Exp(-dt / 0.045));
            _speed += (_targetSpeed - _speed) * (1 - Math.Exp(-dt / 0.12));
            _hoverScale += (_hoverScaleTarget - _hoverScale) * (1 - Math.Exp(-dt / 0.12));
            _hoverTilt += (_hoverTiltTarget - _hoverTilt) * (1 - Math.Exp(-dt / 0.1));
            _targetLean *= Math.Exp(-dt / 0.12);
            _targetTilt *= Math.Exp(-dt / 0.12);
            _targetSpeed *= Math.Exp(-dt / 0.55);
            bool calm = !hasMotion
                && _targetSpeed < 0.5 && _speed < 0.5
                && Math.Abs(_targetLean) < 0.05 && Math.Abs(_lean) < 0.1
                && Math.Abs(_targetTilt) < 0.05 && Math.Abs(_tilt) < 0.1
                && _hoverScaleTarget <= 1.0 && Math.Abs(_hoverScale - 1.0) < 0.003
                && Math.Abs(_hoverTilt) < 0.01 && Math.Abs(_hoverTiltTarget) < 0.01
                && _wantedLeft <= 0 && _wantedRight <= 0 && _wantedWheel <= 0 && _wantedBack <= 0 && _wantedForward <= 0
                && _pressLeft < 0.02 && _pressRight < 0.02 && _pressWheel < 0.02 && _pressBack < 0.02 && _pressForward < 0.02;
            if (calm) return;
            _clock += dt;
            _roadPhase += Math.Min(_speed, 12000) * dt * 0.0009;

            double drift = Math.Sin(_clock * 0.75) * 0.5;
            double bob = Math.Sin(_clock * 1.65) * 0.8;
            double lean = _lean + drift;
            double tilt = _tilt + _hoverTilt + drift * 0.6;
            double scale = _hoverScale + Math.Abs(_lean) * 0.002;
            double rotationY = Math.Clamp(-lean, -24, 24);
            double rotationX = Math.Clamp(tilt, -20, 20);
            if (double.IsNaN(_appliedLean) || Math.Abs(rotationY - _appliedLean) > 0.05 || Math.Abs(rotationX - _appliedTilt) > 0.05 || Math.Abs(scale - _appliedScale) > 0.002)
            {
                _appliedLean = rotationY;
                _appliedTilt = rotationX;
                _appliedScale = scale;
                RigProjection.RotationY = rotationY;
                RigProjection.RotationX = rotationX;
                RigTransform.TranslateX = _hoverTilt * 0.8;
                RigTransform.ScaleX = scale;
                RigTransform.ScaleY = _hoverScale;
            }
            RigTransform.TranslateY = Math.Sin(_roadPhase) * Math.Min(1.6, _speed / 2600) + bob;

            double pressEase = 1 - Math.Exp(-dt / 0.035);
            _pressLeft += (_wantedLeft - _pressLeft) * pressEase;
            _pressRight += (_wantedRight - _pressRight) * pressEase;
            _pressWheel += (_wantedWheel - _pressWheel) * pressEase;
            _pressBack += (_wantedBack - _pressBack) * pressEase;
            _pressForward += (_wantedForward - _pressForward) * pressEase;
            LeftPress.Opacity = _pressLeft * 0.6;
            RightPress.Opacity = _pressRight * 0.6;
            WheelPress.Opacity = _pressWheel * 0.95;
            BackPress.Opacity = _pressBack * 0.95;
            ForwardPress.Opacity = _pressForward * 0.95;

            if (_speed > 30) ScrollRoad(dt);
        }

        private void Stage_PointerEntered(object sender, PointerRoutedEventArgs e) => _hoverScaleTarget = 1.04;

        private void Stage_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _hoverScaleTarget = 1;
            _hoverTiltTarget = 0;
        }

        private void Stage_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (StageHost is null) return;
            double width = Math.Max(1, StageHost.ActualWidth);
            double normalized = e.GetCurrentPoint(StageHost).Position.X / width - 0.5;
            _hoverTiltTarget = Math.Clamp(normalized * 8, -5, 5);
        }

        private void Stage_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (RigTransform == null || MouseRig == null) return;
            var point = e.GetCurrentPoint(MouseRig);
            var kind = point.Properties.PointerUpdateKind;
            bool left = kind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed || point.Properties.IsLeftButtonPressed;
            bool right = kind == Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed || point.Properties.IsRightButtonPressed;
            bool middle = kind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed || point.Properties.IsMiddleButtonPressed;
            double x = point.Position.X;
            double y = point.Position.Y;
            double width = MouseRig.ActualWidth;
            double height = MouseRig.ActualHeight;
            bool inside = x >= 0 && y >= 0 && x <= width && y <= height;
            _pressInsideRig = inside;
            if (!inside) return;
            bool backButton = point.Properties.IsXButton1Pressed;
            bool forwardButton = point.Properties.IsXButton2Pressed;
            if (right) _wantedRight = 1;
            else if (backButton && !forwardButton) _wantedBack = 1;
            else if (forwardButton && !backButton) _wantedForward = 1;
            else if (backButton && forwardButton) { _wantedBack = 1; _wantedForward = 1; }
            else if (middle || (left && InOverlay(WheelPress, point.Position))) _wantedWheel = 1;
            else if (left && InOverlay(ForwardPress, point.Position)) _wantedForward = 1;
            else if (left && InOverlay(BackPress, point.Position)) _wantedBack = 1;
            else if (left && InOverlay(LeftPress, point.Position)) _wantedLeft = 1;
            else if (left && InOverlay(RightPress, point.Position)) _wantedRight = 1;
            else if (left && x < width * 0.5) _wantedLeft = 1;
            else if (left) _wantedRight = 1;
        }

        private void Stage_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (RigTransform == null) return;
            ClearPressTargets();
            if (_pressInsideRig && e.GetCurrentPoint(this).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
            {
                ViewModel.RegisterModelClick();
                ViewModel.RegisterClick();
            }
            _pressInsideRig = false;
        }

        private void Stage_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (RigTransform == null) return;
            ClearPressTargets();
            _pressInsideRig = false;
        }

        private void ClearPressTargets()
        {
            _wantedLeft = 0;
            _wantedRight = 0;
            _wantedWheel = 0;
            _wantedBack = 0;
            _wantedForward = 0;
        }

        private bool InOverlay(FrameworkElement element, Point position)
        {
            if (MouseRig is null || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
            try
            {
                Rect bounds = element.TransformToVisual(MouseRig).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                return bounds.Contains(position);
            }
            catch { return false; }
        }

        private void Stage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (WheelPress == null) return;
            _wantedWheel = 1;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _wantedWheel = 0);
        }

        private void OnPressedButtons(int mask)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnPressedButtons(mask));
                return;
            }
            if (LeftPress == null) return;
            _wantedLeft = (mask & 1) != 0 ? 1 : 0;
            _wantedRight = (mask & 2) != 0 ? 1 : 0;
            _wantedWheel = (mask & 4) != 0 ? 1 : 0;
            _wantedBack = (mask & 8) != 0 ? 1 : 0;
            _wantedForward = (mask & 16) != 0 ? 1 : 0;
        }

        private void OnMotion(InputMotionSample sample)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnMotion(sample));
                return;
            }
            if (RigTransform == null) return;
            if (sample.Hertz > 0)
            {
                _history.Add(sample.Hertz);
                while (_history.Count > 63) _history.RemoveAt(0);
                DrawSpark();
            }
            else if (_history.Count > 0)
            {
                _history.Clear();
                DrawSpark();
            }
        }

        private void Road_SizeChanged(object sender, SizeChangedEventArgs e) => BuildRoad();

        private void BuildRoad()
        {
            if (RoadCanvas == null) return;
            RoadCanvas.Children.Clear();
            _dashes.Clear();
            _streaks.Clear();
            double width = RoadCanvas.ActualWidth;
            double height = RoadCanvas.ActualHeight;
            if (width <= 0 || height <= 0) return;
            double laneY = height - 16;
            Brush laneBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            Brush accentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            var lane = new Rectangle { Width = width, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = laneBrush, Opacity = 0.25 };
            Canvas.SetLeft(lane, 0);
            Canvas.SetTop(lane, laneY);
            RoadCanvas.Children.Add(lane);
            int count = Math.Max(6, (int)(width / 74));
            for (int i = 0; i < count; i++)
            {
                var dash = new Rectangle { Width = 34, Height = 6, RadiusX = 3, RadiusY = 3, Fill = laneBrush, Opacity = 0.55 };
                Canvas.SetTop(dash, laneY - 1.5);
                RoadCanvas.Children.Add(dash);
                _dashes.Add(dash);
            }
            for (int i = 0; i < 6; i++)
            {
                var streak = new Line { Stroke = accentBrush, StrokeThickness = 2, Opacity = 0.1 };
                RoadCanvas.Children.Add(streak);
                _streaks.Add(streak);
            }
            _roadOffset = 0;
            LayoutRoad(width);
        }

        private void LayoutRoad(double width)
        {
            if (_dashes.Count == 0) return;
            double gap = width / (_dashes.Count + 1);
            for (int i = 0; i < _dashes.Count; i++)
            {
                double x = (i * gap + _roadOffset) % width;
                Canvas.SetLeft(_dashes[i], x - 17);
            }
            double intensity = Math.Min(1, _speed / 3500);
            for (int i = 0; i < _streaks.Count; i++)
            {
                Line streak = _streaks[i];
                streak.Opacity = 0.06 + intensity * 0.35;
                double y = 6 + (i % 3) * 10;
                streak.Y1 = y;
                streak.Y2 = y;
                double x = ((_roadOffset * (1.4 + i * 0.25)) + i * 131) % (width + 140) - 70;
                streak.X1 = x;
                streak.X2 = x + 36 + intensity * 64;
            }
        }

        private void ScrollRoad(double dt)
        {
            double width = RoadCanvas?.ActualWidth ?? 0;
            if (width <= 0) return;
            double step = Math.Min(_speed, 12000) * dt * 0.3;
            if (step < 0.6) return;
            _roadOffset = (_roadOffset + step) % width;
            LayoutRoad(width);
        }

        private void Spark_SizeChanged(object sender, SizeChangedEventArgs e) => BuildSpark();

        private void BuildSpark()
        {
            if (SparkCanvas == null) return;
            SparkCanvas.Children.Clear();
            _sparkLine = null;
            double width = SparkCanvas.ActualWidth;
            double height = SparkCanvas.ActualHeight;
            if (width <= 0 || height <= 0) return;
            Brush axisBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            for (int i = 0; i <= 2; i++)
            {
                double y = height * i / 2.0;
                var line = new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = axisBrush, StrokeThickness = 1, Opacity = 0.22 };
                SparkCanvas.Children.Add(line);
            }
            _sparkLine = new Polyline
            {
                Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                StrokeThickness = 2,
            };
            SparkCanvas.Children.Add(_sparkLine);
            DrawSpark();
        }

        private void DrawSpark()
        {
            if (_sparkLine == null) return;
            double width = SparkCanvas.ActualWidth;
            double height = SparkCanvas.ActualHeight;
            if (width <= 0 || height <= 0) return;
            _sparkLine.Points.Clear();
            if (_history.Count < 2) return;
            double max = 1;
            foreach (double value in _history)
                if (value > max) max = value;
            max *= 1.15;
            double step = width / 62.0;
            int offset = 62 - _history.Count;
            for (int i = 0; i < _history.Count; i++)
            {
                double y = height - Math.Clamp(_history[i] / max, 0, 1) * (height - 8) - 4;
                _sparkLine.Points.Add(new Windows.Foundation.Point((offset + i) * step, y));
            }
        }
    }
}
