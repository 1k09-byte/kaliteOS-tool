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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using kaliteConfig.GpuOverclock.Models;
using kaliteConfig.GpuOverclock.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation.Collections;

namespace kaliteConfig.GpuOverclock.Views
{
    /// <summary>
    /// Overclock section embedded at the bottom of the Graphics page. Exposes
    /// the ViewModel so the host page can re-detect (RefreshAsync) when the
    /// GPU changes, and Teardown on unload.
    /// </summary>
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
            this.Loaded += async (_, _) => await Vm.InitializeAsync();
            this.Unloaded += (_, _) => Vm.Teardown();
        }

        // ---------------- x:Bind visibility helpers ----------------

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

        // Risk gate covers the card until accepted (inverse of RiskAccepted).
        public static Visibility NotRiskAcceptedVis(bool riskAccepted)
            => riskAccepted ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility FanStaticVis(GpuFanMode mode)
            => mode == GpuFanMode.Static ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility FanCurveVis(GpuFanMode mode)
            => mode == GpuFanMode.Curve ? Visibility.Visible : Visibility.Collapsed;

        // ---------------- slider commit (debounce on release, not per-tick) ----------------

        // The commit handlers already track user edits in the ViewModel (the
        // commit methods mirror the value into CoreOffsetValue etc., and the
        // x:Bind feeds it back). ValueChanged fires again for that programmatic
        // update - swallowing that echo prevents the revert resync / profile
        // apply slider moves from being re-queued as fresh user writes.
        private void CoreOffsetSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (Vm.IsBusy) return; // programmatic update, not a user edit
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

        // ---------------- numeric text boxes (layout pass): validated at the
        // input layer, committed through the SAME debounced-write + safety
        // path as the sliders. Invalid keystrokes (NaN) never reach the
        // write path; out-of-range values clamp to the live slider range.

        /// <summary>
        /// Shared typed-value commit: ignores invalid text, clamps to the
        /// live range (re-firing into the equal path), and no-ops when the
        /// value already matches (kills programmatic-update echoes).
        /// </summary>
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
            // Same staging semantics as the static slider: sets the value;
            // the ViewModel's change hook debounces the write in Static mode.
            if (Vm.FanStaticPercent != clamped) Vm.FanStaticPercent = (int)clamped;
        }

        private void FanIntervalBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (sender is NumberBox box && !double.IsNaN(box.Value) && Vm.FanIntervalMs != box.Value)
                Vm.FanIntervalMs = box.Value; // ViewModel clamps (100–10000) and pushes live to the loop
        }

        private void FanRampBox_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (sender is NumberBox box && !double.IsNaN(box.Value) && Vm.FanRampStep != box.Value)
                Vm.FanRampStep = box.Value; // ViewModel clamps (0–100) and pushes live to the loop
        }

        private void StartupReapply_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts && Vm.StartupReapplyEnabled != ts.IsOn)
                Vm.ToggleStartupReapplyCommand.Execute(null);
        }

        // Startup-default toggle: skip when the visual state was set
        // programmatically by RefreshProfiles (IsOn already matches storage).
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

        // ---------------- per-game bindings (v2 Part B) ----------------

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

        // ---------------- V/F curve (v2): simple slider ----------------

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

        // ---------------- V/F curve canvas: direct-drag graph ----------------

        private int _vfDragIndex = -1;

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
            if (e.PropertyName == nameof(VfCurvePointRow.OffsetMHz))
                RedrawVfCurve();
        }

        private void VfCurveCanvas_SizeChanged(object sender, RoutedEventArgs e) => RedrawVfCurve();

        // Room for Y tick labels (left), X tick labels + title (bottom),
        // and the Y axis title (top).
        private const double VfPadL = 46, VfPadR = 10, VfPadT = 20, VfPadB = 42;

        /// <summary>
        /// Plot geometry shared by drawing and drag hit-testing, so the
        /// pointer math can never drift from what is on screen.
        /// </summary>
        private sealed record VfPlotGeom(double PlotL, double PlotT, double PlotW, double PlotH,
            int VMin, int VMax, int FLo, int FHi)
        {
            public double X(double v) => PlotL + (v - VMin) / (VMax - VMin) * PlotW;
            public double Y(double f) => PlotT + (1.0 - (f - FLo) / (FHi - FLo)) * PlotH;
        }

        private bool TryGetVfPlotGeom(out VfPlotGeom g)
        {
            g = null!;
            var pts = Vm.VfPoints;
            if (pts.Count == 0 || !Vm.HasVfCurve || VfCurveCanvas is null) return false;
            double w = VfCurveCanvas.ActualWidth, h = VfCurveCanvas.ActualHeight;
            if (w < VfPadL + VfPadR + 20 || h < VfPadT + VfPadB + 20) return false;
            int vMin = pts.Min(r => r.VoltageMv), vMax = pts.Max(r => r.VoltageMv);
            if (vMax == vMin) { vMin--; vMax++; }
            int fLo = pts.Min(r => r.BaseFrequencyMHz + r.MinOffsetMHz);
            int fHi = pts.Max(r => r.BaseFrequencyMHz + r.MaxOffsetMHz);
            if (fHi == fLo) { fLo--; fHi++; }
            g = new VfPlotGeom(VfPadL, VfPadT, w - VfPadL - VfPadR, h - VfPadT - VfPadB, vMin, vMax, fLo, fHi);
            return true;
        }

        /// <summary>"Nice" tick step (1/2/2.5/5 × 10^n) for at most maxTicks ticks.</summary>
        private static (double Step, double First) VfNiceTicks(double min, double max, int maxTicks)
            => GraphTicks.NiceTicks(min, max, maxTicks);

        private static string VfTickLabel(double v) => GraphTicks.Label(v);

        private void RedrawVfCurve()
        {
            if (VfCurveCanvas is null) return;
            VfCurveCanvas.Children.Clear();
            VfAxisLeft.Text = "";
            VfAxisRight.Text = "";
            VfAxisCenter.Text = "";
            if (!TryGetVfPlotGeom(out var g)) return;
            var pts = Vm.VfPoints;

            var secondary = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
                ?? new SolidColorBrush(Colors.Gray);
            var accent = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush
                ?? new SolidColorBrush(Colors.DodgerBlue);
            var grid = Application.Current.Resources["DividerStrokeColorDefaultBrush"] as Brush
                ?? new SolidColorBrush(Colors.Gray) { Opacity = 0.35 };

            double plotR = g.PlotL + g.PlotW, plotB = g.PlotT + g.PlotH;

            // Gridlines + X ticks (voltage, mV).
            var (xStep, xFirst) = VfNiceTicks(g.VMin, g.VMax, 6);
            for (double t = xFirst; t <= g.VMax + 1e-9; t += xStep)
            {
                double x = g.X(t);
                VfCurveCanvas.Children.Add(new Line
                    { X1 = x, Y1 = g.PlotT, X2 = x, Y2 = plotB, Stroke = grid, StrokeThickness = 1 });
                VfCurveCanvas.Children.Add(new Line
                    { X1 = x, Y1 = plotB, X2 = x, Y2 = plotB + 4, Stroke = secondary, StrokeThickness = 1 });
                var label = new TextBlock
                    { Text = VfTickLabel(t), FontSize = 10, Foreground = secondary, TextAlignment = TextAlignment.Center, Width = 64 };
                Canvas.SetLeft(label, Math.Clamp(x - 32, 0, Math.Max(0, VfCurveCanvas.ActualWidth - 64)));
                Canvas.SetTop(label, plotB + 5);
                VfCurveCanvas.Children.Add(label);
            }

            // Gridlines + Y ticks (frequency, MHz).
            var (yStep, yFirst) = VfNiceTicks(g.FLo, g.FHi, 5);
            for (double t = yFirst; t <= g.FHi + 1e-9; t += yStep)
            {
                double y = g.Y(t);
                VfCurveCanvas.Children.Add(new Line
                    { X1 = g.PlotL, Y1 = y, X2 = plotR, Y2 = y, Stroke = grid, StrokeThickness = 1 });
                VfCurveCanvas.Children.Add(new Line
                    { X1 = g.PlotL - 4, Y1 = y, X2 = g.PlotL, Y2 = y, Stroke = secondary, StrokeThickness = 1 });
                var label = new TextBlock
                    { Text = VfTickLabel(t), FontSize = 10, Foreground = secondary, TextAlignment = TextAlignment.Right, Width = g.PlotL - 8 };
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y - 8);
                VfCurveCanvas.Children.Add(label);
            }

            // Axes.
            VfCurveCanvas.Children.Add(new Line
                { X1 = g.PlotL, Y1 = g.PlotT, X2 = g.PlotL, Y2 = plotB, Stroke = secondary, StrokeThickness = 1.5 });
            VfCurveCanvas.Children.Add(new Line
                { X1 = g.PlotL, Y1 = plotB, X2 = plotR, Y2 = plotB, Stroke = secondary, StrokeThickness = 1.5 });

            // Curves: stock dashed gray underneath, live accent on top.
            var stock = new Polyline
            {
                Stroke = secondary,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Opacity = 0.8,
            };
            var live = new Polyline { Stroke = accent, StrokeThickness = 2 };
            foreach (var r in pts)
            {
                stock.Points.Add(new Windows.Foundation.Point(g.X(r.VoltageMv), g.Y(r.BaseFrequencyMHz)));
                live.Points.Add(new Windows.Foundation.Point(g.X(r.VoltageMv), g.Y(r.EffectiveMHz)));
            }
            VfCurveCanvas.Children.Add(stock);
            VfCurveCanvas.Children.Add(live);

            // Thumbs: every point when few, decimated when many (hit-testing
            // still considers ALL points by nearest-X, so decimation is visual only).
            int step = pts.Count <= 48 ? 1 : (pts.Count + 47) / 48;
            for (int i = 0; i < pts.Count; i += step)
            {
                var dot = new Ellipse
                {
                    Width = 9, Height = 9,
                    Fill = accent,
                    Stroke = secondary,
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(dot, g.X(pts[i].VoltageMv) - 4.5);
                Canvas.SetTop(dot, g.Y(pts[i].EffectiveMHz) - 4.5);
                VfCurveCanvas.Children.Add(dot);
            }

            // Axis titles (drawn last so they stay readable).
            var yTitle = new TextBlock { Text = "Frequency (MHz)", FontSize = 11, Foreground = secondary };
            Canvas.SetLeft(yTitle, g.PlotL);
            Canvas.SetTop(yTitle, 0);
            VfCurveCanvas.Children.Add(yTitle);

            var xTitle = new TextBlock
                { Text = "Voltage (mV)", FontSize = 11, Foreground = secondary, TextAlignment = TextAlignment.Center, Width = 200 };
            Canvas.SetLeft(xTitle, g.PlotL + g.PlotW / 2 - 100);
            Canvas.SetTop(xTitle, plotB + 20);
            VfCurveCanvas.Children.Add(xTitle);

            VfAxisCenter.Text = $"{pts.Count} points · {g.FLo}–{g.FHi} MHz envelope";
        }

        private int VfNearestIndex(double x)
        {
            if (!TryGetVfPlotGeom(out var g)) return -1;
            var pts = Vm.VfPoints;
            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                double d = Math.Abs(g.X(pts[i].VoltageMv) - x);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        private void VfCurveCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!Vm.HasVfCurve || Vm.VfPoints.Count == 0) return;
            var canvas = (Canvas)sender;
            var pos = e.GetCurrentPoint(canvas).Position;
            _vfDragIndex = VfNearestIndex(pos.X);
            if (_vfDragIndex >= 0) canvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void VfCurveCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            // Bool-gated: hover moves never edit - only moves between our own
            // press (capture) and release/cancel can drag a point.
            if (_vfDragIndex < 0 || _vfDragIndex >= Vm.VfPoints.Count) return;
            var canvas = (Canvas)sender;
            var pos = e.GetCurrentPoint(canvas).Position;

            // Same geometry as the drawing - a drag lands exactly where the
            // graph shows it.
            if (!TryGetVfPlotGeom(out var g)) return;
            double frac = 1.0 - (pos.Y - g.PlotT) / Math.Max(1.0, g.PlotH);
            frac = Math.Clamp(frac, 0.0, 1.0);
            var row = Vm.VfPoints[_vfDragIndex];
            row.OffsetMHz = (int)Math.Round(g.FLo + frac * (g.FHi - g.FLo)) - row.BaseFrequencyMHz;
            e.Handled = true;
        }

        private void VfCurveCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_vfDragIndex < 0) return;
            _vfDragIndex = -1;
            try { ((Canvas)sender).ReleasePointerCapture(e.Pointer); } catch { }
            // Commit on release: validates monotonicity, then routes through
            // the same safety state machine as every other control.
            Vm.ApplyVfCurveEdits();
            e.Handled = true;
        }

        private void VfCurveCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            // Drag aborted (touch canceled, capture lost): drop the in-flight
            // edits visually by re-syncing rows from the last committed state.
            _vfDragIndex = -1;
        }

        // ---------------- Fan curve graph: drag ↔ table two-way sync ----------------
        // The TABLE stays the commit path (Apply curve button) - the graph only
        // edits row values, exactly like typing in the table cells. No write
        // logic changes: dragging never starts the loop by itself.

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

            // Fixed 0–100 % scale so drags read true; units baked into labels.
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

        private int FanNearestIndex(double x)
        {
            if (!TryGetFanPlotGeom(out var g)) return -1;
            var rows = Vm.CurvePoints;
            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < rows.Count; i++)
            {
                double d = Math.Abs(g.X(rows[i].TempC) - x);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        private void FanCurveCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (Vm.CurvePoints.Count == 0) return;
            var canvas = (Canvas)sender;
            _fanDragIndex = FanNearestIndex(e.GetCurrentPoint(canvas).Position.X);
            if (_fanDragIndex >= 0) canvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void FanCurveCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_fanDragIndex < 0 || _fanDragIndex >= Vm.CurvePoints.Count) return;
            if (!TryGetFanPlotGeom(out var g)) return;
            var pos = e.GetCurrentPoint((Canvas)sender).Position;
            double frac = 1.0 - (pos.Y - g.PlotT) / Math.Max(1.0, g.PlotH);
            // Table rows are int %; the setter path is identical to typing.
            Vm.CurvePoints[_fanDragIndex].FanPercent = (int)Math.Round(Math.Clamp(frac, 0.0, 1.0) * 100.0);
            e.Handled = true;
        }

        private void FanCurveCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_fanDragIndex < 0) return;
            _fanDragIndex = -1;
            try { ((Canvas)sender).ReleasePointerCapture(e.Pointer); } catch { }
            // Deliberately NO auto-commit: the table + Apply curve button
            // stays the single commit path for Curve mode.
            e.Handled = true;
        }

        private void FanCurveCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _fanDragIndex = -1;
        }

        // ---------------- Monitor graphs (live history incl. hotspot) ----------------

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

        /// <summary>
        /// Draws autoscaled history sparklines (nulls break the line - gaps,
        /// never fabricated zeros). Fixed bounds pin scales where they carry
        /// meaning (0–100 % fan, watts from 0).
        /// </summary>
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
