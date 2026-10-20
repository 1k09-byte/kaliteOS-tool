using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.GpuOverclock.Models;
using kaliteConfig.GpuOverclock.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace kaliteConfig.GpuOverclock.Views
{
    public sealed partial class OverclockSection : UserControl
    {
        public OverclockViewModel Vm { get; }

        public OverclockSection()
        {
            this.InitializeComponent();
            Vm = new OverclockViewModel();
            Vm.VfPoints.CollectionChanged += VfPoints_CollectionChanged;
            Vm.CurvePoints.CollectionChanged += FanCurvePoints_CollectionChanged;
            foreach (var r in Vm.CurvePoints)
                r.PropertyChanged += FanCurveRow_PropertyChanged;
            Vm.MonitorUpdated += RedrawMonitor;
            Vm.PropertyChanged += Vm_PropertyChanged;
            this.Loaded += async (_, _) => await Vm.InitializeAsync();
            this.Unloaded += (_, _) => Vm.Teardown();
        }

        public static Visibility NotSupportedVis(bool isSupported)
            => isSupported ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility BoolToVis(bool value)
            => value ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility InverseBoolToVis(bool value)
            => value ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility TextToVis(string text)
            => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility SupportedVis(bool isSupported)
            => isSupported ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility NotRiskAcceptedVis(bool riskAccepted)
            => riskAccepted ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility FanStaticVis(GpuFanMode mode)
            => mode == GpuFanMode.Static ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility FanCurveVis(GpuFanMode mode)
            => mode == GpuFanMode.Curve ? Visibility.Visible : Visibility.Collapsed;

        private void CoreOffsetSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (sender is Slider s && Vm is { IsSupported: true } && Vm.CoreOffsetValue != s.Value)
                Vm.OnCoreOffsetCommitted(s.Value);
        }

        private void MemOffsetSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (sender is Slider s && Vm is { IsSupported: true } && Vm.MemOffsetValue != s.Value)
                Vm.OnMemOffsetCommitted(s.Value);
        }

        private void PowerLimitSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (sender is Slider s && Vm is { IsSupported: true } && Vm.PowerLimitValue != s.Value)
                Vm.OnPowerLimitCommitted(s.Value);
        }

        private void TempLimitSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (sender is Slider s && Vm is { IsSupported: true } && Vm.TempLimitValue != s.Value)
                Vm.OnTempLimitCommitted(s.Value);
        }

        private void FanStaticSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (sender is Slider s && Vm.FanStaticPercent != s.Value)
                Vm.FanStaticPercent = (int)s.Value;
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OverclockViewModel.HasVfCurve))
            {
                if (Vm.HasVfCurve) DispatcherQueue.TryEnqueue(RedrawVfCurve);
                return;
            }

            if (e.PropertyName != nameof(OverclockViewModel.ZeroRpmEnabled)) return;
            if (_syncingZeroRpm) return;

            _syncingZeroRpm = true;
            try
            {
                ZeroRpmToggle.IsOn = Vm.ZeroRpmEnabled;
            }
            finally
            {
                _syncingZeroRpm = false;
            }
        }

        private bool _syncingZeroRpm;

        private void ZeroRpmToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch t) return;
            if (_syncingZeroRpm) return;

            bool requested = t.IsOn;
            _ = ApplyZeroRpmAsync(requested, t);
        }

        private async Task ApplyZeroRpmAsync(bool enabled, ToggleSwitch toggle)
        {
            await Vm.SetZeroRpmAsync(enabled);

            _syncingZeroRpm = true;
            try
            {
                toggle.IsOn = Vm.ZeroRpmEnabled;
            }
            finally
            {
                _syncingZeroRpm = false;
            }
        }

        private void CommitNumberBox(object sender, double min, double max, double current, Action<double> commit)
        {
            if (Vm.IsBusy) return;
            if (sender is not NumberBox box) return;
            double v = box.Value;
            if (double.IsNaN(v)) return;
            double clamped = Math.Clamp(v, min, max);
            if (clamped != v) { box.Value = clamped; return; }
            if (current != clamped) commit(clamped);
        }

        private void CoreOffsetBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (Vm is { IsSupported: true, HasCoreOffset: true })
                CommitNumberBox(sender, Vm.CoreOffsetMin, Vm.CoreOffsetMax, Vm.CoreOffsetValue, Vm.OnCoreOffsetCommitted);
        }

        private void MemOffsetBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (Vm is { IsSupported: true, HasMemOffset: true })
                CommitNumberBox(sender, Vm.MemOffsetMin, Vm.MemOffsetMax, Vm.MemOffsetValue, Vm.OnMemOffsetCommitted);
        }

        private void PowerLimitBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (Vm is { IsSupported: true, HasPowerLimit: true })
                CommitNumberBox(sender, Vm.PowerLimitMin, Vm.PowerLimitMax, Vm.PowerLimitValue, Vm.OnPowerLimitCommitted);
        }

        private void TempLimitBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (Vm is { IsSupported: true, HasTempLimit: true })
                CommitNumberBox(sender, Vm.TempLimitMin, Vm.TempLimitMax, Vm.TempLimitValue, Vm.OnTempLimitCommitted);
        }

        private void VfFlatBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (Vm is { IsSupported: true, HasVfCurve: true })
                CommitNumberBox(sender, Vm.VfFlatMin, Vm.VfFlatMax, Vm.VfFlatValue, Vm.OnVfFlatCommitted);
        }

        private void FanStaticBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (sender is not NumberBox box) return;
            if (double.IsNaN(box.Value)) return;
            double clamped = Math.Clamp(box.Value, 0, 100);
            if (clamped != box.Value) { box.Value = clamped; return; }
            if (Vm.FanStaticPercent != clamped) Vm.FanStaticPercent = (int)clamped;
        }

        private void FanIntervalBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (sender is NumberBox box && !double.IsNaN(box.Value) && Vm.FanIntervalMs != box.Value)
                Vm.FanIntervalMs = box.Value;
        }

        private void FanRampBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (sender is NumberBox box && !double.IsNaN(box.Value) && Vm.FanRampStep != box.Value)
                Vm.FanRampStep = box.Value;
        }

        private void StartupReapply_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts && Vm.StartupReapplyEnabled != ts.IsOn)
                Vm.ToggleStartupReapplyCommand.Execute(null);
        }

        private void StartupDefault_Toggled(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is OverclockProfile p
                && sender is ToggleSwitch ts
                && p.IsStartupDefault != ts.IsOn)
            {
                Vm.SetStartupDefaultCommand.Execute(p);
            }
        }

        private void ApplyProfile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is OverclockProfile p)
                _ = Vm.ApplyProfileAsync(p);
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is OverclockProfile p)
                Vm.DeleteProfileCommand.Execute(p);
        }

        private void BindProfile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is OverclockProfile p)
                _ = Vm.BindProfileToGameAsync(p);
        }

        private void RemoveBinding_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is GameBindingRow r)
                Vm.RemoveBindingCommand.Execute(r);
        }

        private void BindingEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is GameBindingRow r
                && sender is ToggleSwitch ts
                && r.Enabled != ts.IsOn)
            {
                Vm.SetBindingEnabled(r, ts.IsOn);
            }
        }

        private void ClearBindingPath_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is GameBindingRow r)
                Vm.ClearBindingPathCommand.Execute(r);
        }

        private void LockBindingPath_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is GameBindingRow r)
                _ = Vm.LockBindingPathAsync(r);
        }

        private void VfFlatSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (sender is Slider s && Vm is { IsSupported: true, HasVfCurve: true } && Vm.VfFlatValue != s.Value)
                Vm.OnVfFlatCommitted(s.Value);
        }

        private void VoltageBoostSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (sender is Slider s && Vm is { IsSupported: true, HasVoltageBoost: true } && Vm.VoltageBoostPercentValue != s.Value)
                Vm.OnVoltageBoostPercentCommitted(s.Value);
        }

        private void ResetVfCurve_Click(object sender, RoutedEventArgs e) => Vm.ResetVfCurve();

        private void ApplyVfCurve_Click(object sender, RoutedEventArgs e) => Vm.ApplyVfCurveEdits();

        private int _vfDragIndex = -1;
        private int _vfHoverIndex = -1;
        private double _vfGrabDelta;
        private int[]? _vfDragSnapshot;
        private VfPlotGeom? _vfGeom;
        private bool _vfChromeValid;

        private readonly List<int> _vfKnots = new();
        private readonly List<Ellipse> _vfHandles = new();
        private Polyline? _vfStockLine;
        private Polyline? _vfLiveLine;
        private Polygon? _vfBand;
        private Line? _vfGuideV;
        private Line? _vfGuideH;
        private Border? _vfReadoutChip;
        private TextBlock? _vfReadoutText;
        private Brush _vfNodeFill = new SolidColorBrush(Colors.White);
        private Brush _vfNodeActiveFill = new SolidColorBrush(Colors.DodgerBlue);

        private static Brush VfBrush(string key, Windows.UI.Color fallback)
            => Application.Current.Resources[key] as Brush ?? new SolidColorBrush(fallback);

        private void VfCurveCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _vfDragIndex = -1;
            _vfDragSnapshot = null;
            if (!Vm.HasVfCurve || Vm.VfPoints.Count == 0 || _vfGeom is null) return;
            var canvas = (Canvas)sender;
            var pos = e.GetCurrentPoint(canvas).Position;
            int knot = VfHitTest(pos);
            if (knot < 0 || knot >= _vfKnots.Count) return;
            int idx = _vfKnots[knot];
            if (idx >= Vm.VfPoints.Count) return;
            var row = Vm.VfPoints[idx];
            _vfDragIndex = idx;
            _vfDragSnapshot = Vm.VfPoints.Select(r => r.OffsetMHz).ToArray();
            _vfGrabDelta = pos.Y - _vfGeom.Y(row.EffectiveMHz);
            canvas.CapturePointer(e.Pointer);
            UpdateVfDynamic();
            e.Handled = true;
        }

        private void VfCurveCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var canvas = (Canvas)sender;
            var point = e.GetCurrentPoint(canvas);
            if (_vfDragIndex < 0)
            {
                UpdateVfHover(point.Position, canvas);
                return;
            }
            if (!point.IsInContact) return;
            ApplyVfDrag(point.Position);
            e.Handled = true;
        }

        private void VfCurveCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_vfHoverIndex < 0) return;
            _vfHoverIndex = -1;
            if (sender is VfEditorCanvas vf) vf.ShowHandCursor(false);
            UpdateVfDynamic();
        }

        private void VfPoints_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (VfCurvePointRow r in e.OldItems)
                    r.PropertyChanged -= VfRow_PropertyChanged;
            if (e.NewItems != null)
                foreach (VfCurvePointRow r in e.NewItems)
                    r.PropertyChanged += VfRow_PropertyChanged;
            RedrawVfCurve();
        }

        private void VfRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(VfCurvePointRow.OffsetMHz)) return;
            if (_vfDragIndex >= 0) return;
            UpdateVfDynamic();
        }

        private void VfCurveCanvas_SizeChanged(object sender, RoutedEventArgs e) => RedrawVfCurve();

        private const double VfPadL = 64, VfPadR = 18, VfPadT = 34, VfPadB = 52;
        private const int VfMaxHandles = 21;

        private sealed record VfPlotGeom(double PlotL, double PlotT, double PlotW, double PlotH,
            int VMin, int VMax, int FLo, int FHi)
        {
            public double X(double v) => PlotL + (v - VMin) / Math.Max(1.0, (double)(VMax - VMin)) * PlotW;
            public double Y(double f) => PlotT + (1.0 - (f - FLo) / Math.Max(1.0, (double)(FHi - FLo))) * PlotH;
        }

        private bool TryGetVfPlotGeom(out VfPlotGeom g)
        {
            g = null!;
            var pts = Vm.VfPoints;
            if (pts.Count == 0 || !Vm.HasVfCurve || VfCurveCanvas is null) return false;
            double w = VfCurveCanvas.ActualWidth;
            double h = VfCurveCanvas.ActualHeight;
            if (w < VfPadL + VfPadR + 40 || h < VfPadT + VfPadB + 40) return false;

            int vMin = pts.Min(r => r.VoltageMv);
            int vMax = pts.Max(r => r.VoltageMv);
            if (vMax == vMin) { vMin -= 25; vMax += 25; }

            int baseLo = pts.Min(r => r.BaseFrequencyMHz);
            int baseHi = pts.Max(r => r.BaseFrequencyMHz);
            int margin = (int)Math.Clamp((baseHi - baseLo) * 0.22, 140, 420);
            int fLo = Math.Min(baseLo - margin, pts.Min(r => r.EffectiveMHz) - 40);
            int fHi = Math.Max(baseHi + margin, pts.Max(r => r.EffectiveMHz) + 40);
            if (fHi - fLo < 200) { int mid = (fLo + fHi) / 2; fLo = mid - 100; fHi = mid + 100; }

            g = new VfPlotGeom(VfPadL, VfPadT, w - VfPadL - VfPadR, h - VfPadT - VfPadB, vMin, vMax, fLo, fHi);
            return true;
        }

        private void BuildVfKnots()
        {
            _vfKnots.Clear();
            int n = Vm.VfPoints.Count;
            if (n == 0) return;
            if (n <= VfMaxHandles)
            {
                for (int i = 0; i < n; i++) _vfKnots.Add(i);
                return;
            }
            int gaps = VfMaxHandles - 1;
            for (int k = 0; k < gaps; k++)
            {
                int idx = (int)Math.Round(k * (n - 1) / (double)gaps);
                if (_vfKnots.Count == 0 || idx != _vfKnots[^1]) _vfKnots.Add(idx);
            }
            if (_vfKnots[^1] != n - 1) _vfKnots.Add(n - 1);
        }

        private void RedrawVfCurve()
        {
            if (VfCurveCanvas is null) return;
            VfCurveCanvas.Children.Clear();
            _vfHandles.Clear();
            _vfStockLine = null;
            _vfLiveLine = null;
            _vfBand = null;
            _vfGuideV = null;
            _vfGuideH = null;
            _vfReadoutChip = null;
            _vfReadoutText = null;
            _vfChromeValid = false;
            _vfGeom = null;
            _vfHoverIndex = -1;
            BuildVfKnots();
            if (!TryGetVfPlotGeom(out var g))
            {
                VfAxisCenter.Text = "";
                return;
            }
            _vfGeom = g;
            DrawVfChrome(g);
            _vfChromeValid = true;
            UpdateVfDynamic();
        }

        private void DrawVfChrome(VfPlotGeom g)
        {
            var secondary = VfBrush("TextFillColorSecondaryBrush", Colors.Gray);
            var tertiary = VfBrush("TextFillColorTertiaryBrush", Colors.Gray);
            var accent = VfBrush("AccentFillColorDefaultBrush", Colors.DodgerBlue);
            var accentSoft = VfBrush("AccentFillColorTertiaryBrush", Colors.DodgerBlue);
            var grid = VfBrush("DividerStrokeColorDefaultBrush", Colors.Gray);
            _vfNodeFill = VfBrush("SolidBackgroundFillColorBaseBrush", Colors.White);
            _vfNodeActiveFill = accent;

            double plotR = g.PlotL + g.PlotW, plotB = g.PlotT + g.PlotH;

            var (fyStep, fyFirst) = GraphTicks.NiceTicks(g.FLo, g.FHi, 6);
            for (double t = fyFirst; t <= g.FHi + 1e-9; t += fyStep)
            {
                double y = g.Y(t);
                if (y < g.PlotT - 0.5 || y > plotB + 0.5) continue;
                VfCurveCanvas.Children.Add(new Line
                    { X1 = g.PlotL, Y1 = y, X2 = plotR, Y2 = y, Stroke = grid, StrokeThickness = 1, Opacity = 0.5 });
                var lbl = new TextBlock
                    { Text = GraphTicks.Label(t), FontSize = 11, Foreground = secondary, TextAlignment = TextAlignment.Right, Width = g.PlotL - 10 };
                Canvas.SetLeft(lbl, 0);
                Canvas.SetTop(lbl, y - 8);
                VfCurveCanvas.Children.Add(lbl);
            }

            var (vxStep, vxFirst) = GraphTicks.NiceTicks(g.VMin, g.VMax, 7);
            for (double t = vxFirst; t <= g.VMax + 1e-9; t += vxStep)
            {
                double x = g.X(t);
                if (x < g.PlotL - 0.5 || x > plotR + 0.5) continue;
                VfCurveCanvas.Children.Add(new Line
                    { X1 = x, Y1 = g.PlotT, X2 = x, Y2 = plotB, Stroke = grid, StrokeThickness = 1, Opacity = 0.5 });
                var lbl = new TextBlock
                    { Text = GraphTicks.Label(t), FontSize = 11, Foreground = secondary, TextAlignment = TextAlignment.Center, Width = 56 };
                Canvas.SetLeft(lbl, Math.Clamp(x - 28, 0, Math.Max(0, VfCurveCanvas.ActualWidth - 56)));
                Canvas.SetTop(lbl, plotB + 7);
                VfCurveCanvas.Children.Add(lbl);
            }

            VfCurveCanvas.Children.Add(new Line
                { X1 = g.PlotL, Y1 = g.PlotT, X2 = g.PlotL, Y2 = plotB, Stroke = secondary, StrokeThickness = 1.25, Opacity = 0.7 });
            VfCurveCanvas.Children.Add(new Line
                { X1 = g.PlotL, Y1 = plotB, X2 = plotR, Y2 = plotB, Stroke = secondary, StrokeThickness = 1.25, Opacity = 0.7 });

            var yTitle = new TextBlock { Text = "Clock (MHz)", FontSize = 11, Foreground = tertiary };
            Canvas.SetLeft(yTitle, 4);
            Canvas.SetTop(yTitle, 8);
            VfCurveCanvas.Children.Add(yTitle);

            var xTitle = new TextBlock
                { Text = "Voltage (mV)", FontSize = 11, Foreground = tertiary, TextAlignment = TextAlignment.Center, Width = 160 };
            Canvas.SetLeft(xTitle, Math.Clamp(g.PlotL + g.PlotW / 2 - 80, 0, Math.Max(0, VfCurveCanvas.ActualWidth - 160)));
            Canvas.SetTop(xTitle, plotB + 27);
            VfCurveCanvas.Children.Add(xTitle);

            _vfStockLine = new Polyline
            {
                Stroke = secondary,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Opacity = 0.55,
                IsHitTestVisible = false,
            };
            foreach (var r in Vm.VfPoints)
                _vfStockLine.Points.Add(new Windows.Foundation.Point(g.X(r.VoltageMv), g.Y(r.BaseFrequencyMHz)));
            VfCurveCanvas.Children.Add(_vfStockLine);

            _vfBand = new Polygon { Fill = accentSoft, Opacity = 0.3, IsHitTestVisible = false };
            VfCurveCanvas.Children.Add(_vfBand);

            _vfLiveLine = new Polyline
            {
                Stroke = accent,
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            };
            VfCurveCanvas.Children.Add(_vfLiveLine);

            _vfGuideV = new Line
                { Stroke = accent, StrokeThickness = 1, Opacity = 0.55, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            _vfGuideH = new Line
                { Stroke = accent, StrokeThickness = 1, Opacity = 0.55, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            VfCurveCanvas.Children.Add(_vfGuideV);
            VfCurveCanvas.Children.Add(_vfGuideH);

            _vfReadoutText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
            _vfReadoutChip = new Border
            {
                Background = VfBrush("CardBackgroundFillColorSecondaryBrush", Colors.Black),
                BorderBrush = VfBrush("CardStrokeColorDefaultBrush", Colors.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Child = _vfReadoutText,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            VfCurveCanvas.Children.Add(_vfReadoutChip);

            foreach (int idx in _vfKnots)
            {
                var h = new Ellipse
                {
                    Width = 11,
                    Height = 11,
                    Fill = _vfNodeFill,
                    Stroke = accent,
                    StrokeThickness = 2.5,
                    IsHitTestVisible = false,
                };
                _vfHandles.Add(h);
                VfCurveCanvas.Children.Add(h);
            }
        }

        private void UpdateVfDynamic()
        {
            if (VfCurveCanvas is null || !_vfChromeValid || _vfGeom is null) return;
            var g = _vfGeom;
            var pts = Vm.VfPoints;
            if (pts.Count == 0 || _vfLiveLine is null || _vfBand is null) return;

            _vfLiveLine.Points.Clear();
            _vfBand.Points.Clear();
            var forward = new List<Windows.Foundation.Point>(pts.Count);
            foreach (var r in pts)
            {
                var p = new Windows.Foundation.Point(g.X(r.VoltageMv), g.Y(r.EffectiveMHz));
                forward.Add(p);
                _vfLiveLine.Points.Add(p);
            }
            foreach (var p in forward) _vfBand.Points.Add(p);
            for (int i = pts.Count - 1; i >= 0; i--)
                _vfBand.Points.Add(new Windows.Foundation.Point(g.X(pts[i].VoltageMv), g.Y(pts[i].BaseFrequencyMHz)));

            for (int k = 0; k < _vfHandles.Count && k < _vfKnots.Count; k++)
            {
                int idx = _vfKnots[k];
                if (idx >= pts.Count) continue;
                var r = pts[idx];
                bool active = idx == _vfDragIndex || idx == _vfHoverIndex;
                var h = _vfHandles[k];
                h.Width = h.Height = active ? 15 : 11;
                h.Fill = active ? _vfNodeActiveFill : _vfNodeFill;
                Canvas.SetLeft(h, g.X(r.VoltageMv) - h.Width / 2);
                Canvas.SetTop(h, g.Y(r.EffectiveMHz) - h.Height / 2);
            }

            UpdateVfReadout(g, _vfDragIndex >= 0 ? _vfDragIndex : _vfHoverIndex);

            VfAxisCenter.Text = $"{pts.Count} nodes · {pts.Min(r => r.EffectiveMHz)}-{pts.Max(r => r.EffectiveMHz)} MHz applied";
        }

        private void UpdateVfReadout(VfPlotGeom g, int idx)
        {
            if (_vfReadoutChip is null || _vfReadoutText is null || _vfGuideV is null || _vfGuideH is null) return;
            if (idx < 0 || idx >= Vm.VfPoints.Count)
            {
                _vfReadoutChip.Visibility = Visibility.Collapsed;
                _vfGuideV.Visibility = Visibility.Collapsed;
                _vfGuideH.Visibility = Visibility.Collapsed;
                return;
            }
            var r = Vm.VfPoints[idx];
            double x = g.X(r.VoltageMv), y = g.Y(r.EffectiveMHz);
            _vfReadoutText.Text = $"{r.VoltageMv} mV · {r.EffectiveMHz} MHz  ({r.OffsetMHz:+0;-0;0})";
            _vfReadoutChip.Visibility = Visibility.Visible;
            _vfReadoutChip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double cw = _vfReadoutChip.DesiredSize.Width;
            double ch = _vfReadoutChip.DesiredSize.Height;
            double cx = Math.Clamp(x + 14, g.PlotL, Math.Max(g.PlotL, VfCurveCanvas.ActualWidth - cw - 6));
            double cy = Math.Clamp(y - ch - 12, 2, Math.Max(2, VfCurveCanvas.ActualHeight - ch - 2));
            Canvas.SetLeft(_vfReadoutChip, cx);
            Canvas.SetTop(_vfReadoutChip, cy);
            _vfGuideV.X1 = x; _vfGuideV.X2 = x; _vfGuideV.Y1 = g.PlotT; _vfGuideV.Y2 = g.PlotT + g.PlotH;
            _vfGuideH.Y1 = y; _vfGuideH.Y2 = y; _vfGuideH.X1 = g.PlotL; _vfGuideH.X2 = g.PlotL + g.PlotW;
            _vfGuideV.Visibility = Visibility.Visible;
            _vfGuideH.Visibility = Visibility.Visible;
        }

        private int VfHitTest(Windows.Foundation.Point pos)
        {
            if (_vfGeom is null || _vfKnots.Count == 0) return -1;
            var g = _vfGeom;
            var pts = Vm.VfPoints;
            int best = -1;
            double bestD = 18 * 18;
            int loose = -1;
            double looseDx = 24;
            for (int k = 0; k < _vfKnots.Count; k++)
            {
                int idx = _vfKnots[k];
                if (idx >= pts.Count) continue;
                var r = pts[idx];
                double dx = g.X(r.VoltageMv) - pos.X;
                double dy = g.Y(r.EffectiveMHz) - pos.Y;
                double d = dx * dx + dy * dy;
                if (d <= bestD) { bestD = d; best = k; }
                double adx = Math.Abs(dx);
                if (adx < looseDx) { looseDx = adx; loose = k; }
            }
            if (best >= 0) return best;
            bool inside = pos.X >= g.PlotL - 10 && pos.X <= g.PlotL + g.PlotW + 10
                          && pos.Y >= g.PlotT - 10 && pos.Y <= g.PlotT + g.PlotH + 10;
            return inside ? loose : -1;
        }

        private void UpdateVfHover(Windows.Foundation.Point pos, Canvas canvas)
        {
            if (_vfGeom is null) return;
            int knot = VfHitTest(pos);
            int hover = knot >= 0 && knot < _vfKnots.Count ? _vfKnots[knot] : -1;
            if (hover == _vfHoverIndex) return;
            _vfHoverIndex = hover;
            if (canvas is VfEditorCanvas vf) vf.ShowHandCursor(hover >= 0);
            UpdateVfDynamic();
        }

        private void ApplyVfDrag(Windows.Foundation.Point pos)
        {
            if (_vfGeom is null || _vfDragIndex < 0 || _vfDragIndex >= Vm.VfPoints.Count) return;
            var g = _vfGeom;
            int idx = _vfDragIndex;
            var pts = Vm.VfPoints;
            var row = pts[idx];

            double frac = 1.0 - ((pos.Y - _vfGrabDelta) - g.PlotT) / Math.Max(1.0, g.PlotH);
            frac = Math.Clamp(frac, 0.0, 1.0);
            int targetEff = (int)Math.Round(g.FLo + frac * (g.FHi - g.FLo));

            int knotPos = _vfKnots.IndexOf(idx);
            int lo = Math.Min(row.MinOffsetMHz, row.MaxOffsetMHz);
            int hi = Math.Max(row.MinOffsetMHz, row.MaxOffsetMHz);
            if (knotPos > 0)
            {
                int lk = _vfKnots[knotPos - 1];
                lo = Math.Max(lo, pts[lk].EffectiveMHz - 1 - row.BaseFrequencyMHz);
            }
            if (knotPos >= 0 && knotPos < _vfKnots.Count - 1)
            {
                int rk = _vfKnots[knotPos + 1];
                hi = Math.Min(hi, pts[rk].EffectiveMHz + 1 - row.BaseFrequencyMHz);
            }

            int want = targetEff - row.BaseFrequencyMHz;
            if (lo <= hi) row.OffsetMHz = Math.Clamp(want, lo, hi);

            ShapeVfSpans(idx);
            UpdateVfDynamic();
        }

        private void ShapeVfSpans(int idx)
        {
            int knotPos = _vfKnots.IndexOf(idx);
            if (knotPos < 0) return;
            if (knotPos > 0) InterpolateVfSpan(_vfKnots[knotPos - 1], idx);
            if (knotPos < _vfKnots.Count - 1) InterpolateVfSpan(idx, _vfKnots[knotPos + 1]);
        }

        private void InterpolateVfSpan(int a, int b)
        {
            if (b <= a + 1) return;
            var pts = Vm.VfPoints;
            double ea = pts[a].EffectiveMHz;
            double eb = pts[b].EffectiveMHz;
            for (int i = a + 1; i < b; i++)
            {
                double t = (i - a) / (double)(b - a);
                int eff = (int)Math.Round(ea + (eb - ea) * t);
                pts[i].OffsetMHz = eff - pts[i].BaseFrequencyMHz;
            }
        }

        private void VfCurveCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_vfDragIndex < 0) return;
            int idx = _vfDragIndex;
            _vfDragIndex = -1;
            try { ((Canvas)sender).ReleasePointerCapture(e.Pointer); } catch { }
            var snap = _vfDragSnapshot;
            _vfDragSnapshot = null;
            if (idx < 0 || idx >= Vm.VfPoints.Count) { RedrawVfCurve(); return; }

            bool changed = false;
            if (snap is not null)
                for (int i = 0; i < snap.Length && i < Vm.VfPoints.Count; i++)
                    if (snap[i] != Vm.VfPoints[i].OffsetMHz) { changed = true; break; }

            if (!changed)
            {
                RedrawVfCurve();
                e.Handled = true;
                return;
            }

            Vm.ApplyVfCurveEdits();
            if (!string.IsNullOrEmpty(Vm.VfCurveError) && snap is not null)
            {
                for (int i = 0; i < snap.Length && i < Vm.VfPoints.Count; i++)
                    Vm.VfPoints[i].OffsetMHz = snap[i];
                Vm.ClearVfCurveError();
            }
            RedrawVfCurve();
            e.Handled = true;
        }

        private void VfCurveCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (_vfDragIndex >= 0)
            {
                var snap = _vfDragSnapshot;
                if (snap is not null)
                    for (int i = 0; i < snap.Length && i < Vm.VfPoints.Count; i++)
                        Vm.VfPoints[i].OffsetMHz = snap[i];
            }
            _vfDragIndex = -1;
            _vfDragSnapshot = null;
            RedrawVfCurve();
        }

        private int _fanDragIndex = -1;

        private sealed record FanPlotGeom(double PlotL, double PlotT, double PlotW, double PlotH,
            int TMin, int TMax)
        {
            public double X(double t) => PlotL + (t - TMin) / (TMax - TMin) * PlotW;
            public double Y(double p) => PlotT + (1.0 - p / 100.0) * PlotH;
        }

        private bool TryGetFanPlotGeom(out FanPlotGeom g)
        {
            g = null!;
            var rows = Vm.CurvePoints;
            if (rows.Count == 0 || FanCurveCanvas is null) return false;
            double w = FanCurveCanvas.ActualWidth, h = FanCurveCanvas.ActualHeight;
            const double padL = 38, padR = 8, padT = 8, padB = 20;
            if (w < padL + padR + 20 || h < padT + padB + 20) return false;
            int tMin = rows.Min(r => r.TempC), tMax = rows.Max(r => r.TempC);
            if (tMax == tMin) { tMin -= 5; tMax += 5; }
            tMin = Math.Max(0, tMin - 2);
            tMax += 2;
            g = new FanPlotGeom(padL, padT, w - padL - padR, h - padT - padB, tMin, tMax);
            return true;
        }

        private void FanCurvePoints_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (FanCurvePointRow r in e.OldItems)
                    r.PropertyChanged -= FanCurveRow_PropertyChanged;
            if (e.NewItems != null)
                foreach (FanCurvePointRow r in e.NewItems)
                    r.PropertyChanged += FanCurveRow_PropertyChanged;
            RedrawFanCurve();
        }

        private void FanCurveRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FanCurvePointRow.FanPercent)
                || e.PropertyName == nameof(FanCurvePointRow.TempC))
                RedrawFanCurve();
        }

        private void FanCurveCanvas_SizeChanged(object sender, RoutedEventArgs e) => RedrawFanCurve();

        private void RedrawFanCurve()
        {
            if (FanCurveCanvas is null) return;
            FanCurveCanvas.Children.Clear();
            if (!TryGetFanPlotGeom(out var g)) return;
            var rows = Vm.CurvePoints.ToList();

            var secondary = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
                ?? new SolidColorBrush(Colors.Gray);
            var accent = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush
                ?? new SolidColorBrush(Colors.DodgerBlue);
            var grid = Application.Current.Resources["DividerStrokeColorDefaultBrush"] as Brush
                ?? new SolidColorBrush(Colors.Gray) { Opacity = 0.35 };

            double plotR = g.PlotL + g.PlotW, plotB = g.PlotT + g.PlotH;

            var (yStep, yFirst) = GraphTicks.NiceTicks(0, 100, 4);
            for (double t = yFirst; t <= 100 + 1e-9; t += yStep)
            {
                double y = g.Y(t);
                FanCurveCanvas.Children.Add(new Line
                    { X1 = g.PlotL, Y1 = y, X2 = plotR, Y2 = y, Stroke = grid, StrokeThickness = 1 });
                var label = new TextBlock
                    { Text = $"{GraphTicks.Label(t)}%", FontSize = 10, Foreground = secondary, TextAlignment = TextAlignment.Right, Width = g.PlotL - 6 };
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y - 8);
                FanCurveCanvas.Children.Add(label);
            }

            var (xStep, xFirst) = GraphTicks.NiceTicks(g.TMin, g.TMax, 5);
            for (double t = xFirst; t <= g.TMax + 1e-9; t += xStep)
            {
                double x = g.X(t);
                FanCurveCanvas.Children.Add(new Line
                    { X1 = x, Y1 = g.PlotT, X2 = x, Y2 = plotB, Stroke = grid, StrokeThickness = 1 });
                var label = new TextBlock
                    { Text = $"{GraphTicks.Label(t)}°", FontSize = 10, Foreground = secondary, TextAlignment = TextAlignment.Center, Width = 48 };
                Canvas.SetLeft(label, Math.Clamp(x - 24, 0, Math.Max(0, FanCurveCanvas.ActualWidth - 48)));
                Canvas.SetTop(label, plotB + 3);
                FanCurveCanvas.Children.Add(label);
            }

            FanCurveCanvas.Children.Add(new Line
                { X1 = g.PlotL, Y1 = g.PlotT, X2 = g.PlotL, Y2 = plotB, Stroke = secondary, StrokeThickness = 1.5 });
            FanCurveCanvas.Children.Add(new Line
                { X1 = g.PlotL, Y1 = plotB, X2 = plotR, Y2 = plotB, Stroke = secondary, StrokeThickness = 1.5 });

            var line = new Polyline { Stroke = accent, StrokeThickness = 2 };
            foreach (var r in rows.OrderBy(r => r.TempC))
                line.Points.Add(new Windows.Foundation.Point(g.X(r.TempC), g.Y(r.FanPercent)));
            FanCurveCanvas.Children.Add(line);

            foreach (var r in rows)
            {
                var dot = new Ellipse { Width = 10, Height = 10, Fill = accent, Stroke = secondary, StrokeThickness = 1 };
                Canvas.SetLeft(dot, g.X(r.TempC) - 5);
                Canvas.SetTop(dot, g.Y(r.FanPercent) - 5);
                FanCurveCanvas.Children.Add(dot);
            }
        }

        private int FanHitTest(Windows.Foundation.Point pos)
        {
            if (!TryGetFanPlotGeom(out var g)) return -1;
            var rows = Vm.CurvePoints;
            int best = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < rows.Count; i++)
            {
                double dx = Math.Abs(g.X(rows[i].TempC) - pos.X);
                double dy = Math.Abs(g.Y(rows[i].FanPercent) - pos.Y);
                if (dx > 28 || dy > 60) continue;
                double d = dx + dy;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        private void FanCurveCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _fanDragIndex = -1;
            if (Vm.CurvePoints.Count == 0) return;
            var canvas = (Canvas)sender;
            int hit = FanHitTest(e.GetCurrentPoint(canvas).Position);
            if (hit < 0) return;
            _fanDragIndex = hit;
            canvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void FanCurveCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_fanDragIndex < 0 || _fanDragIndex >= Vm.CurvePoints.Count) return;
            var point = e.GetCurrentPoint((Canvas)sender);
            if (!point.IsInContact) { _fanDragIndex = -1; return; }
            if (!TryGetFanPlotGeom(out var g)) return;
            var pos = point.Position;
            double frac = 1.0 - (pos.Y - g.PlotT) / Math.Max(1.0, g.PlotH);
            Vm.CurvePoints[_fanDragIndex].FanPercent = (int)Math.Round(Math.Clamp(frac, 0.0, 1.0) * 100.0);
            e.Handled = true;
        }

        private void FanCurveCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_fanDragIndex < 0) return;
            _fanDragIndex = -1;
            try { ((Canvas)sender).ReleasePointerCapture(e.Pointer); } catch { }
            e.Handled = true;
        }

        private void FanCurveCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _fanDragIndex = -1;
        }

        private void MonitorCanvas_SizeChanged(object sender, RoutedEventArgs e) => RedrawMonitor();

        private void RedrawMonitor()
        {
            if (MonitorTempCanvas is null) return;
            var history = Vm.GetMonitorHistory();
            DrawMonitorSeries(MonitorTempCanvas, history, new (Func<MonitorSample, double?> Get, string BrushKey)[]
            {
                (s => (double?)s.GpuTempC, "AccentFillColorDefaultBrush"),
                (s => (double?)s.HotspotTempC, "SystemFillColorCautionBrush"),
                (s => (double?)s.MemTempC, "SystemFillColorSuccessBrush"),
            }, null, null);
            DrawMonitorSeries(MonitorClockCanvas, history, new (Func<MonitorSample, double?> Get, string BrushKey)[]
            {
                (s => s.CoreClockMHz, "AccentFillColorDefaultBrush"),
                (s => s.MemClockMHz, "SystemFillColorSuccessBrush"),
            }, 0, null);
            DrawMonitorSeries(MonitorPowerCanvas, history, new (Func<MonitorSample, double?> Get, string BrushKey)[]
            {
                (s => s.PowerDrawW, "AccentFillColorDefaultBrush"),
            }, 0, null);
            DrawMonitorSeries(MonitorFanCanvas, history, new (Func<MonitorSample, double?> Get, string BrushKey)[]
            {
                (s => s.FanPercent is null ? null : (double?)s.FanPercent.Value, "SystemFillColorSuccessBrush"),
            }, 0, 100);
        }

        private void DrawMonitorSeries(Canvas canvas,
            IReadOnlyList<MonitorSample> history,
            (Func<MonitorSample, double?> Get, string BrushKey)[] series,
            double? fixedMin, double? fixedMax)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            const double padL = 42, padR = 6, padT = 6, padB = 6;
            if (w < padL + padR + 20 || h < padT + padB + 16 || history.Count < 2) return;

            var all = new List<double>();
            foreach (var s in series)
                foreach (var m in history)
                {
                    var v = s.Get(m);
                    if (v.HasValue) all.Add(v.Value);
                }
            if (all.Count == 0) return;
            double lo = fixedMin ?? all.Min(), hi = fixedMax ?? all.Max();
            if (hi <= lo) { hi = lo + 1; if (!fixedMin.HasValue) lo -= 1; }

            double X(int i) => padL + i / (double)(history.Count - 1) * (w - padL - padR);
            double Y(double v) => padT + (1.0 - (v - lo) / (hi - lo)) * (h - padT - padB);

            var secondary = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
                ?? new SolidColorBrush(Colors.Gray);
            var fallback = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush
                ?? new SolidColorBrush(Colors.DodgerBlue);
            var grid = Application.Current.Resources["DividerStrokeColorDefaultBrush"] as Brush
                ?? new SolidColorBrush(Colors.Gray) { Opacity = 0.35 };

            var (step, first) = GraphTicks.NiceTicks(lo, hi, 3);
            for (double t = first; t <= hi + 1e-9; t += step)
            {
                double y = Y(t);
                canvas.Children.Add(new Line
                    { X1 = padL, Y1 = y, X2 = w - padR, Y2 = y, Stroke = grid, StrokeThickness = 1 });
                var label = new TextBlock
                    { Text = GraphTicks.Label(t), FontSize = 9, Foreground = secondary, TextAlignment = TextAlignment.Right, Width = padL - 4 };
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y - 7);
                canvas.Children.Add(label);
            }

            foreach (var (get, key) in series)
            {
                var brush = Application.Current.Resources[key] as Brush ?? fallback;
                var run = new List<Windows.Foundation.Point>();
                void Flush()
                {
                    if (run.Count > 1)
                    {
                        var pl = new Polyline { Stroke = brush, StrokeThickness = 1.5 };
                        foreach (var p in run) pl.Points.Add(p);
                        canvas.Children.Add(pl);
                    }
                    else if (run.Count == 1)
                    {
                        var d = new Ellipse { Width = 3, Height = 3, Fill = brush };
                        Canvas.SetLeft(d, run[0].X - 1.5);
                        Canvas.SetTop(d, run[0].Y - 1.5);
                        canvas.Children.Add(d);
                    }
                    run.Clear();
                }
                for (int i = 0; i < history.Count; i++)
                {
                    var v = get(history[i]);
                    if (!v.HasValue) { Flush(); continue; }
                    run.Add(new Windows.Foundation.Point(X(i), Y(v.Value)));
                }
                Flush();
            }
        }
    }
}
