using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using kaliteConfig.GpuOverclock.Models;
using kaliteConfig.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Windows.Foundation;

namespace kaliteConfig.Pages
{
    public sealed partial class BenchmarkPage : Page
    {
        public BenchmarkViewModel ViewModel => App.Current.Benchmark;

        private List<(double Min, double Max)> _buckets = new();
        private int _bucketFrames;
        private double _selectedStutterMs = double.NaN;
        private DispatcherQueueTimer? _liveTimer;

        public BenchmarkPage()
        {
            this.InitializeComponent();
            this.Loaded += (_, _) =>
            {
                ViewModel.PropertyChanged += Vm_PropertyChanged;
                FrameCanvas.SizeChanged += (_, _) => RedrawFrame();
                HistCanvas.SizeChanged += (_, _) => RedrawHist();
                CompareCanvas.SizeChanged += (_, _) => RedrawCompare();
                LiveCanvas.SizeChanged += (_, _) => TailLive();
                _liveTimer = DispatcherQueue.CreateTimer();
                _liveTimer.Interval = TimeSpan.FromSeconds(1);
                _liveTimer.Tick += (_, _) => TailLive();
                // Auto A/B finished: switch to the Compare tab so the user
                // lands on the results immediately.
                ViewModel.AutoAbNavigateRequested += () => DispatcherQueue.TryEnqueue(() =>
                {
                    MainSelectorBar.SelectedItem = TabCompare;
                });
            };
            this.Unloaded += (_, _) => ViewModel.PropertyChanged -= Vm_PropertyChanged;
        }

        public static Brush Res(string key, Windows.UI.Color fallback)
        {
            return Application.Current.Resources[key] as Brush ?? new SolidColorBrush(fallback);
        }

        private static Brush ThemeBrush(string key)
        {
            try
            {
                if (Application.Current.Resources.TryGetValue(key, out var value)
                    && value is Brush brush)
                    return brush;
            }
            catch { }
            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private void BenchmarkCard_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                card.Background = ThemeBrush("SubtleFillColorSecondaryBrush");
                card.BorderBrush = ThemeBrush("AccentFillColorDefaultBrush");
            }
        }

        private void BenchmarkCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                card.Background = ThemeBrush("CardBackgroundFillColorDefaultBrush");
                card.BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush");
            }
        }

        private void BenchmarkCard_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is Models.TunerProcessRow target)
            {
                ViewModel.SelectedTarget = target;
                e.Handled = false;
            }
            else if (element.DataContext is Models.BenchmarkRun run)
            {
                ViewModel.SelectedRun = run;
                e.Handled = false;
            }
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BenchmarkViewModel.SelectedStats))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateTiles();
                    _selectedStutterMs = double.NaN;
                    StutterList.ItemsSource = ViewModel.SelectedStats?.StutterEvents;
                    RedrawFrame();
                    RedrawHist();
                });
            }
            else if (e.PropertyName == nameof(BenchmarkViewModel.VerdictText))
            {
                DispatcherQueue.TryEnqueue(RedrawCompare);
            }
            else if (e.PropertyName == nameof(BenchmarkViewModel.ShowSensorOverlay))
            {
                DispatcherQueue.TryEnqueue(RedrawFrame);
            }
            else if (e.PropertyName == nameof(BenchmarkViewModel.IsCapturing))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ViewModel.IsCapturing) { ResetLiveTail(); try { _liveTimer?.Start(); } catch { } }
                    else { try { _liveTimer?.Stop(); } catch { } LiveCanvas.Children.Clear(); ResetLiveTail(); }
                });
            }
        }

        private void UpdateTiles()
        {
            var s = ViewModel.SelectedStats;
            StatAvg.Text = s == null ? "-" : $"{s.AverageFps:F0}";
            StatP1.Text = s == null ? "-" : $"{s.P1Fps:F0}";
            StatP02.Text = s == null ? "-" : $"{s.P02Fps:F0}";
            StatLow1.Text = s == null ? "-" : $"{s.Low1CountFps:F0}";
            StatMinMax.Text = s == null ? "-" : $"{s.MinFps:F0}/{s.MaxFps:F0}";
            StatStutter.Text = s == null ? "-" : $"{s.StutterCount} ({s.StutterPct:F1}%)";
        }

        private void MainSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            CapturePanel.Visibility = sender.SelectedItem == TabCapture ? Visibility.Visible : Visibility.Collapsed;
            RunsPanel.Visibility = sender.SelectedItem == TabRuns ? Visibility.Visible : Visibility.Collapsed;
            AnalysisPanel.Visibility = sender.SelectedItem == TabAnalysis ? Visibility.Visible : Visibility.Collapsed;
            ComparePanel.Visibility = sender.SelectedItem == TabCompare ? Visibility.Visible : Visibility.Collapsed;
            ExportPanel.Visibility = sender.SelectedItem == TabExport ? Visibility.Visible : Visibility.Collapsed;
            if (sender.SelectedItem == TabAnalysis) { UpdateTiles(); RedrawFrame(); RedrawHist(); }
            if (sender.SelectedItem == TabCompare) RedrawCompare();
        }

        // ─── Frametime chart (min/max buckets) ──────────────────────
        private void RedrawFrame()
        {
            FrameCanvas.Children.Clear();
            var run = ViewModel.SelectedRun;
            if (run?.FrametimesMs == null || run.FrametimesMs.Length < 2) return;
            double w = FrameCanvas.ActualWidth, h = FrameCanvas.ActualHeight;
            const double padL = 44, padR = 8, padT = 8, padB = 26; // padB holds the time axis
            if (w < padL + padR + 20 || h < padT + padB + 16) return;

            _buckets = BenchmarkDecimate.MinMaxBuckets(run.FrametimesMs, 2000);
            _bucketFrames = run.FrametimesMs.Length;
            double lo = 0; // CapFrameX anchors the Y axis at zero
            double hi = _buckets.Max(b => b.Max);
            if (hi <= lo) hi = lo + 1;
            // Y-Axis scale: clamp the view like CapFrameX "Full fit" vs caps.
            double? cap = YScaleCombo.SelectedIndex switch
            {
                1 => 10.0,
                2 => 20.0,
                3 => 50.0,
                _ => (double?)null,
            };
            if (cap.HasValue && hi > cap.Value) hi = cap.Value;
            if (hi - lo < 1) hi = lo + 1;

            double X(int i) => padL + i / (double)(_buckets.Count - 1) * (w - padL - padR);
            double Y(double v) => padT + (1.0 - (v - lo) / (hi - lo)) * (h - padT - padB);

            var secondary = Res("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray);
            var tertiary = Res("TextFillColorTertiaryBrush", Microsoft.UI.Colors.Gray);
            var grid = Res("DividerStrokeColorDefaultBrush", Microsoft.UI.Colors.Gray);
            var accent = Res("AccentFillColorDefaultBrush", Microsoft.UI.Colors.DodgerBlue);
            var crit = Res("SystemFillColorCriticalBrush", Microsoft.UI.Colors.Red);

            // X axis: recording time in seconds.
            double totalSec = run.FrametimesMs.Sum(f => f) / 1000.0;
            const int xTicks = 6;
            for (int i = 0; i <= xTicks; i++)
            {
                double frac = i / (double)xTicks;
                double x = padL + frac * (w - padL - padR);
                FrameCanvas.Children.Add(new Line { X1 = x, Y1 = padT, X2 = x, Y2 = h - padB, Stroke = grid, StrokeThickness = 1 });
                var xlab = new TextBlock { Text = (totalSec * frac).ToString("F0"), FontSize = 9, Foreground = secondary, TextAlignment = TextAlignment.Center, Width = 30 };
                Canvas.SetLeft(xlab, x - 15);
                Canvas.SetTop(xlab, h - padB + 4);
                FrameCanvas.Children.Add(xlab);
            }
            var xTitle = new TextBlock { Text = "Recording time [s]", FontSize = 10, Foreground = secondary, TextAlignment = TextAlignment.Center, Width = 120 };
            Canvas.SetLeft(xTitle, padL + (w - padL - padR) / 2 - 60);
            Canvas.SetTop(xTitle, h - padB + 14);
            FrameCanvas.Children.Add(xTitle);

            var (step, first) = GraphTicks.NiceTicks(lo, hi, 4);
            for (double t = first; t <= hi + 1e-9; t += step)
            {
                double y = Y(t);
                FrameCanvas.Children.Add(new Line { X1 = padL, Y1 = y, X2 = w - padR, Y2 = y, Stroke = grid, StrokeThickness = 1 });
                var label = new TextBlock { Text = GraphTicks.Label(t), FontSize = 9, Foreground = secondary, TextAlignment = TextAlignment.Right, Width = padL - 4 };
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y - 7);
                FrameCanvas.Children.Add(label);
            }

            // Moving average first (so bars draw over it, CapFrameX style).
            double rollWin = Math.Max(1, _buckets.Count / 50);
            var ma = new PointCollection();
            for (int i = 0; i < _buckets.Count; i++)
            {
                int a = Math.Max(0, i - (int)rollWin), b = Math.Min(_buckets.Count - 1, i + (int)rollWin);
                double m = 0; int cnt = 0;
                for (int k = a; k <= b; k++) { m += (_buckets[k].Min + _buckets[k].Max) / 2.0; cnt++; }
                m /= cnt;
                ma.Add(new Point(X(i), Y(Math.Clamp(m, lo, hi))));
            }
            if (ma.Count > 1)
            {
                FrameCanvas.Children.Add(new Polyline
                {
                    Points = ma,
                    Stroke = Res("TextFillColorTertiaryBrush", Microsoft.UI.Colors.Gray),
                    StrokeThickness = 2,
                });
            }

            // Min/max verticals per bucket (spikes survive decimation).
            for (int i = 0; i < _buckets.Count; i++)
            {
                double x = X(i);
                FrameCanvas.Children.Add(new Line
                {
                    X1 = x, Y1 = Y(Math.Min(_buckets[i].Max, hi)), X2 = x, Y2 = Math.Max(Y(Math.Min(_buckets[i].Min, hi)), Y(Math.Min(_buckets[i].Max, hi)) + 1),
                    Stroke = accent, StrokeThickness = 1,
                });
            }

            // Average line.
            var stats = ViewModel.SelectedStats;
            if (stats != null && stats.AverageFps > 0)
            {
                double avgMs = 1000.0 / stats.AverageFps;
                if (avgMs >= lo && avgMs <= hi)
                {
                    double y = Y(avgMs);
                    FrameCanvas.Children.Add(new Line
                    {
                        X1 = padL, Y1 = y, X2 = w - padR, Y2 = y,
                        Stroke = Res("SystemFillColorSuccessBrush", Microsoft.UI.Colors.Green),
                        StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
                    });
                }
            }

            // Stutter ticks + throttle ticks + selected marker.
            if (stats != null && stats.TotalTimeMs > 0)
            {
                foreach (var ev in stats.StutterEvents.Take(500))
                {
                    double x = padL + ev.TimestampMs / stats.TotalTimeMs * (w - padL - padR);
                    FrameCanvas.Children.Add(new Line { X1 = x, Y1 = padT, X2 = x, Y2 = padT + 8, Stroke = crit, StrokeThickness = 2 });
                }
                var runSensors = run.Sensors;
                if (runSensors != null && runSensors.Count > 1)
                {
                    var sum = Services.BenchmarkSensorStats.Summarize(runSensors);
                    var amber = Res("SystemFillColorCautionBrush", Microsoft.UI.Colors.Orange);
                    foreach (double frac in sum.ThrottleFracs.Take(500))
                    {
                        double x = padL + Math.Clamp(frac, 0, 1) * (w - padL - padR);
                        FrameCanvas.Children.Add(new Line { X1 = x, Y1 = h - padB - 8, X2 = x, Y2 = h - padB, Stroke = amber, StrokeThickness = 2 });
                    }
                }
                if (!double.IsNaN(_selectedStutterMs))
                {
                    double x = padL + _selectedStutterMs / stats.TotalTimeMs * (w - padL - padR);
                    FrameCanvas.Children.Add(new Line { X1 = x, Y1 = padT, X2 = x, Y2 = h - padB, Stroke = crit, StrokeThickness = 1.5 });
                }
            }

            // Optional GPU-util overlay (normalized 0..100 to chart height, dashed).
            if (ViewModel.ShowSensorOverlay && run.Sensors != null && run.Sensors.Count > 1)
            {
                var series = Services.BenchmarkSensorStats.GpuUtilSeries(run.Sensors, _buckets.Count);
                var gray = Res("TextFillColorTertiaryBrush", Microsoft.UI.Colors.Gray);
                var pts = new PointCollection();
                for (int i = 0; i < series.Count && i < _buckets.Count; i++)
                {
                    if (double.IsNaN(series[i])) continue;
                    double x = padL + i / (double)(_buckets.Count - 1) * (w - padL - padR);
                    double y = padT + (1.0 - Math.Clamp(series[i], 0, 100) / 100.0) * (h - padT - padB);
                    pts.Add(new Point(x, y));
                }
                if (pts.Count > 1)
                {
                    FrameCanvas.Children.Add(new Polyline
                    {
                        Points = pts, Stroke = gray, StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 5, 4 },
                    });
                }
            }
        }

        private void YScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RedrawFrame();
        }

        private void FrameCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var run = ViewModel.SelectedRun;
            if (run?.FrametimesMs == null || run.FrametimesMs.Length == 0 || _buckets.Count == 0) return;
            Point p = e.GetCurrentPoint(FrameCanvas).Position;
            const double padL = 44, padR = 8;
            double w = FrameCanvas.ActualWidth;
            double frac = Math.Clamp((p.X - padL) / Math.Max(1, w - padL - padR), 0, 1);
            int frame = Math.Min(run.FrametimesMs.Length - 1, (int)(frac * run.FrametimesMs.Length));
            float ms = run.FrametimesMs[frame];
            string text = $"frame {frame}: {ms:F2} ms · {1000.0 / Math.Max(0.01, ms):F0} fps";
            if (run.Sensors != null && run.Sensors.Count > 0)
            {
                var s = Services.BenchmarkSensorStats.NearestAt(run.Sensors, frac);
                if (s != null)
                {
                    string gpu = s.GpuUtilPct.HasValue ? $"{s.GpuUtilPct:F0}% gpu" : "gpu n/a";
                    string cpu = s.CpuPct.HasValue ? $"{s.CpuPct:F0}% cpu" : "cpu n/a";
                    string tmp = s.GpuTempC.HasValue ? $"{s.GpuTempC:F0}°" : "";
                    text += $" · {gpu} · {cpu} {tmp}".TrimEnd();
                }
            }
            HoverText.Text = text;
        }

        private void FrameCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            HoverText.Text = string.Empty;
        }

        private void StutterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StutterList.SelectedItem is Models.StutterEvent ev)
            {
                _selectedStutterMs = ev.TimestampMs;
                RedrawFrame();
            }
        }

        // ─── Live mini-graph (tails the in-progress CSV, 1 Hz) ──────
        private readonly List<float> _liveFrames = new();
        private long _liveOffset;

        private void TailLive()
        {
            try
            {
                string? path = ViewModel.LiveCsvPath;
                if (!ViewModel.IsCapturing || string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                    return;
                // Incremental: parse only bytes appended since the last tick.
                // The old full re-parse was O(n²) over the capture and perturbed
                // the machine being measured.
                var (chunk, newOffset) = Services.BenchmarkPresentMonCsv.ParseFileTail(
                    path, ViewModel.LivePid, _liveOffset);
                _liveOffset = newOffset;
                _liveFrames.AddRange(chunk.FrametimesMs);
                if (_liveFrames.Count < 2) return;
                var frames = _liveFrames;
                LiveCanvas.Children.Clear();
                double w = LiveCanvas.ActualWidth, h = LiveCanvas.ActualHeight;
                const double padL = 44, padR = 8, padT = 6, padB = 6;
                if (w < padL + padR + 20 || h < padT + padB + 14) return;
                var buckets = ViewModels.BenchmarkDecimate.MinMaxBuckets(frames, 400);
                double lo = buckets.Min(b => b.Min), hi = buckets.Max(b => b.Max);
                if (hi <= lo) hi = lo + 1;
                var accent = Res("AccentFillColorDefaultBrush", Microsoft.UI.Colors.DodgerBlue);
                for (int i = 0; i < buckets.Count; i++)
                {
                    double x = padL + i / (double)(buckets.Count - 1) * (w - padL - padR);
                    double y1 = padT + (1.0 - (buckets[i].Max - lo) / (hi - lo)) * (h - padT - padB);
                    double y2 = padT + (1.0 - (buckets[i].Min - lo) / (hi - lo)) * (h - padT - padB);
                    LiveCanvas.Children.Add(new Line
                    {
                        X1 = x, Y1 = y1, X2 = x, Y2 = Math.Max(y1 + 1, y2),
                        Stroke = accent, StrokeThickness = 1,
                    });
                }
                float last = frames[^1];
                var tag = new TextBlock
                {
                    Text = $"{1000.0 / Math.Max(0.01, last):F0} fps · {frames.Count} frames",
                    FontSize = 11, Foreground = Res("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray),
                };
                Canvas.SetLeft(tag, padL + 4);
                Canvas.SetTop(tag, 2);
                LiveCanvas.Children.Add(tag);
            }
            catch
            {
                // File locked mid-write or half line: skip this tick.
            }
        }

        private void ResetLiveTail()
        {
            _liveFrames.Clear();
            _liveOffset = 0;
        }

        // ─── PNG report (Analysis tab → shareable card) ─────────────
        private async void SavePngReport_Click(object sender, RoutedEventArgs e)
        {
            if (AnalysisPanel.Visibility != Visibility.Visible
                || ViewModel.SelectedRun == null || ViewModel.SelectedStats == null)
            {
                ViewModel.StatusText = "Open a run in the Analysis tab first.";
                return;
            }
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                if (App.MainWindow != null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                }
                picker.FileTypeChoices.Add("PNG image", new[] { ".png" });
                picker.SuggestedFileName = $"{ViewModel.SelectedRun.Game}-{ViewModel.SelectedRun.Date:yyyyMMdd-HHmmss}-report";
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;

                var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await rtb.RenderAsync(AnalysisContent);
                var pixels = await rtb.GetPixelsAsync();
                byte[] bytes;
                using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels))
                {
                    bytes = new byte[pixels.Length];
                    reader.ReadBytes(bytes);
                }
                using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                    Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96,
                    bytes);
                await encoder.FlushAsync();
                ViewModel.StatusText = "PNG report saved.";
            }
            catch (Exception ex)
            {
                ViewModel.StatusText = $"PNG export failed: {ex.Message}";
            }
        }

        // ─── Histogram ──────────────────────────────────────────────
        private void RedrawHist()
        {
            HistCanvas.Children.Clear();
            var run = ViewModel.SelectedRun;
            if (run?.FrametimesMs == null || run.FrametimesMs.Length < 2) return;
            double w = HistCanvas.ActualWidth, h = HistCanvas.ActualHeight;
            const double padL = 44, padR = 8, padT = 8, padB = 18;
            if (w < padL + padR + 20 || h < padT + padB + 16) return;

            const int bins = 40;
            double minF = run.FrametimesMs.Min(f => 1000.0 / Math.Max(0.01f, f));
            double maxF = run.FrametimesMs.Max(f => 1000.0 / Math.Max(0.01f, f));
            if (maxF <= minF) maxF = minF + 1;
            int[] counts = new int[bins];
            foreach (float f in run.FrametimesMs)
            {
                double fps = 1000.0 / Math.Max(0.01, f);
                int b = Math.Min(bins - 1, (int)((fps - minF) / (maxF - minF) * bins));
                counts[b]++;
            }
            int peak = Math.Max(1, counts.Max());
            var accent = Res("AccentFillColorDefaultBrush", Microsoft.UI.Colors.DodgerBlue);
            var secondary = Res("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray);
            var grid = Res("DividerStrokeColorDefaultBrush", Microsoft.UI.Colors.Gray);
            double bw = (w - padL - padR) / bins;
            for (int i = 0; i < bins; i++)
            {
                double bh = counts[i] / (double)peak * (h - padT - padB);
                var r = new Rectangle
                {
                    Width = Math.Max(1, bw - 1), Height = Math.Max(1, bh),
                    Fill = accent, Opacity = 0.8,
                };
                Canvas.SetLeft(r, padL + i * bw);
                Canvas.SetTop(r, h - padB - bh);
                HistCanvas.Children.Add(r);
            }
            var (step, first) = GraphTicks.NiceTicks(minF, maxF, 4);
            for (double t = first; t <= maxF + 1e-9; t += step)
            {
                double x = padL + (t - minF) / (maxF - minF) * (w - padL - padR);
                HistCanvas.Children.Add(new Line { X1 = x, Y1 = padT, X2 = x, Y2 = h - padB, Stroke = grid, StrokeThickness = 1 });
                var label = new TextBlock { Text = GraphTicks.Label(t), FontSize = 9, Foreground = secondary, TextAlignment = TextAlignment.Center, Width = 40 };
                Canvas.SetLeft(label, x - 20);
                Canvas.SetTop(label, h - padB + 2);
                HistCanvas.Children.Add(label);
            }
        }

        // ─── Compare bars (PresentMon-viewer style) ─────────────────
        // Metric groups × per-run rounded bars, FPS x-axis, value labels, legend.
        private void RedrawCompare()
        {
            CompareCanvas.Children.Clear();
            var rows = ViewModel.CompareRows;
            if (rows.Count == 0) return;
            double w = CompareCanvas.ActualWidth;
            if (w < 200) w = 600;

            var groups = new (string Label, Func<CompareRow, double> Get)[]
            {
                ("0.1% Low Avg", r => r.Low01Count),
                ("1% Low Avg", r => r.Low1Count),
                ("Avg (Arithmetic)", r => r.AvgArith),
                ("Avg (Harmonic)", r => r.AvgHarm),
                ("P50 (Median)", r => r.Median),
            };

            const double padL = 132, padR = 64, legendH = 38, axisH = 30;
            const double groupLabelH = 26, barH = 22, barGap = 9, groupGap = 22;
            int n = Math.Min(6, rows.Count);
            double groupH = groupLabelH + n * barH + (n - 1) * barGap + groupGap;
            double plotH = legendH + groups.Length * groupH + axisH;
            CompareCanvas.Height = plotH;

            var list = rows.Take(n).ToList();
            CompareTitle.Text = list[0].Name.Contains(".exe", StringComparison.OrdinalIgnoreCase)
                ? list[0].Name : (ViewModel.BaselineRun?.Exe ?? list[0].Name);

            double maxV = 1;
            foreach (var g in groups)
                foreach (var r in list)
                    maxV = Math.Max(maxV, g.Get(r));

            var secondary = Res("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray);
            var primary = Res("TextFillColorPrimaryBrush", Microsoft.UI.Colors.White);
            var grid = Res("DividerStrokeColorDefaultBrush", Microsoft.UI.Colors.Gray);

            // Legend.
            double lx = padL, ly = 6;
            foreach (var r in list)
            {
                string label = r.IsBaseline ? r.ShortLabel + " (base)" : r.ShortLabel;
                double tw = Math.Min(190, 8 + label.Length * 6.2 + 16);
                if (lx + tw > w - padR && lx > padL)
                {
                    lx = padL;
                    ly += 16;
                }
                var sw = new Rectangle { Width = 12, Height = 12, Fill = new SolidColorBrush(ParseColor(r.ColorHex)) };
                Canvas.SetLeft(sw, lx);
                Canvas.SetTop(sw, ly);
                CompareCanvas.Children.Add(sw);
                var t = new TextBlock
                {
                    Text = label, FontSize = 12, Foreground = secondary,
                    Width = tw - 18, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Canvas.SetLeft(t, lx + 16);
                Canvas.SetTop(t, ly - 4);
                CompareCanvas.Children.Add(t);
                lx += tw;
            }

            // X axis (FPS).
            var (step, first) = GraphTicks.NiceTicks(0, maxV * 1.05, 12);
            double X(double v) => padL + v / (maxV * 1.05) * (w - padL - padR);
            for (double t = first; t <= maxV * 1.05 + 1e-9; t += step)
            {
                double x = X(t);
                CompareCanvas.Children.Add(new Line
                {
                    X1 = x, Y1 = legendH, X2 = x, Y2 = plotH - axisH,
                    Stroke = grid, StrokeThickness = 1,
                });
                var label = new TextBlock
                {
                    Text = GraphTicks.Label(t), FontSize = 10, Foreground = secondary,
                    TextAlignment = TextAlignment.Center, Width = 48,
                };
                Canvas.SetLeft(label, x - 24);
                Canvas.SetTop(label, plotH - axisH + 5);
                CompareCanvas.Children.Add(label);
            }
            var axisTitle = new TextBlock
            {
                Text = "FPS", FontSize = 11, Foreground = secondary,
                TextAlignment = TextAlignment.Center, Width = 60,
            };
            Canvas.SetLeft(axisTitle, padL + (w - padL - padR) / 2 - 30);
            Canvas.SetTop(axisTitle, plotH - 14);
            CompareCanvas.Children.Add(axisTitle);

            // Groups × bars.
            for (int g = 0; g < groups.Length; g++)
            {
                double gy = legendH + g * groupH;
                var glabel = new TextBlock
                {
                    Text = groups[g].Label, FontSize = 13, Foreground = secondary,
                    Width = padL - 8, TextAlignment = TextAlignment.Right,
                };
                Canvas.SetLeft(glabel, 0);
                Canvas.SetTop(glabel, gy);
                CompareCanvas.Children.Add(glabel);

                for (int i = 0; i < n; i++)
                {
                    var r = list[i];
                    double v = Math.Max(0, groups[g].Get(r));
                    double y = gy + groupLabelH + i * (barH + barGap);
                    double bw = Math.Max(2, v / (maxV * 1.05) * (w - padL - padR));
                    var fill = new Rectangle
                    {
                        Width = bw, Height = barH,
                        RadiusX = 4, RadiusY = 4,
                        Fill = new SolidColorBrush(ParseColor(r.ColorHex)),
                    };
                    Canvas.SetLeft(fill, padL);
                    Canvas.SetTop(fill, y);
                    CompareCanvas.Children.Add(fill);
                    // Run name inside the bar when it fits, else the legend
                    // above carries it (bar order matches legend order).
                    string shortName = r.ShortLabel + (r.IsBaseline ? " (base)" : "");
                    if (bw > 150)
                    {
                        var inlabel = new TextBlock
                        {
                            Text = shortName, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
                            Width = bw - 12, TextTrimming = TextTrimming.CharacterEllipsis,
                        };
                        Canvas.SetLeft(inlabel, padL + 6);
                        Canvas.SetTop(inlabel, y + 2);
                        CompareCanvas.Children.Add(inlabel);
                    }
                    var vlabel = new TextBlock
                    {
                        Text = $"{v:F1}", FontSize = 12, Foreground = primary,
                    };
                    Canvas.SetLeft(vlabel, padL + bw + 6);
                    Canvas.SetTop(vlabel, y);
                    CompareCanvas.Children.Add(vlabel);
                }
            }
        }

        private static Windows.UI.Color ParseColor(string colorHex)
        {
            try
            {
                string hex = colorHex.TrimStart('#');
                if (hex.Length == 6)
                    return Windows.UI.Color.FromArgb(255,
                        Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16));
            }
            catch { }
            return Microsoft.UI.Colors.DodgerBlue;
        }

    }
}
