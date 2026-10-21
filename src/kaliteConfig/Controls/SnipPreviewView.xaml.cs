using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Models;
using kaliteConfig.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace kaliteConfig.Controls;

public sealed partial class SnipPreviewView : UserControl
{
    private const double CrossfadeMilliseconds = 220;

    private const double WheelZoomStep = 1.15;

    private readonly SnipPreviewViewport _viewport = new();
    private readonly Stopwatch _fadeClock = new();

    private CanvasBitmap? _placeholderBitmap;
    private CanvasBitmap? _fullBitmap;
    private CanvasVirtualBitmap? _virtualBitmap;

    private byte[]? _placeholderPixels;
    private int _placeholderWidth, _placeholderHeight, _placeholderTier;
    private byte[]? _fullPixels;
    private int _fullWidth, _fullHeight;

    private CancellationTokenSource? _cts;
    private DispatcherQueueTimer? _fadeTimer;
    private string? _loadedPath;
    private string? _pendingPath;
    private int _generation;
    private double _crossfade = 1;
    private double _lastDpiScale = -1;
    private bool _autoFit = true;
    private bool _dragging;
    private Point _dragOrigin;
    private bool _blank;

    public SnipPreviewView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateZoomChrome();
    }

    public event EventHandler? PreviewActivated;

    public event EventHandler? ImageLoaded;

    public event EventHandler<string>? LoadFailed;

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(SnipPreviewView),
        new PropertyMetadata(string.Empty, OnSourcePathChanged));

    public static readonly DependencyProperty WheelZoomEnabledProperty = DependencyProperty.Register(
        nameof(WheelZoomEnabled), typeof(bool), typeof(SnipPreviewView), new PropertyMetadata(false));

    public string SourcePath
    {
        get => (string)(GetValue(SourcePathProperty) ?? string.Empty);
        set => SetValue(SourcePathProperty, value);
    }

    public bool WheelZoomEnabled
    {
        get => (bool)GetValue(WheelZoomEnabledProperty);
        set => SetValue(WheelZoomEnabledProperty, value);
    }

    public bool DoubleTapTogglesZoom { get; set; }

    public double ZoomPercent => _viewport.Zoom * 100;
    public string ZoomLabelText => _viewport.ZoomLabel;
    public bool HasImage => _viewport.HasImage;
    public int ImagePixelWidth => _viewport.ImagePixelWidth;
    public int ImagePixelHeight => _viewport.ImagePixelHeight;
    public SnipPreviewViewport Viewport => _viewport;

    public void ZoomToFit()
    {
        _autoFit = true;
        SyncViewport();
        _viewport.Fit();
        UpdateZoomChrome();
        PreviewCanvas.Invalidate();
    }

    public void ZoomToActualSize()
    {
        _autoFit = false;
        SyncViewport();
        _viewport.ActualSize();
        UpdateZoomChrome();
        PreviewCanvas.Invalidate();
    }

    public void ZoomBy(double factor)
    {
        _autoFit = false;
        SyncViewport();
        _viewport.ZoomAt(_viewport.ViewportWidthDip / 2, _viewport.ViewportHeightDip / 2, factor);
        UpdateZoomChrome();
        PreviewCanvas.Invalidate();
    }

    private static void OnSourcePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SnipPreviewView)d).HandleSourcePathChanged(e.NewValue as string);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is { } root)
        {
            root.Changed -= XamlRoot_Changed;
            root.Changed += XamlRoot_Changed;
        }

        SyncViewport();
        _lastDpiScale = CurrentDpiScale;

        var path = string.IsNullOrWhiteSpace(_pendingPath) ? SourcePath : _pendingPath;
        _pendingPath = null;
        if (!string.IsNullOrWhiteSpace(path)) _ = LoadAsync(path, resetView: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _generation++;
        try { _cts?.Cancel(); } catch { }
        _fadeTimer?.Stop();
        ReleaseImageResources();
        if (XamlRoot is { } root) root.Changed -= XamlRoot_Changed;
    }

    private void HandleSourcePathChanged(string? path)
    {
        if (!IsLoaded)
        {
            _pendingPath = path;
            return;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            _generation++;
            try { _cts?.Cancel(); } catch { }
            _loadedPath = null;
            ReleaseImageResources();
            _viewport.SetImage(0, 0);
            SetStatus(string.Empty, busy: false, error: false);
            SetBlank(false);
            PreviewCanvas.Invalidate();
            return;
        }
        _ = LoadAsync(path, resetView: true);
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        var scale = CurrentDpiScale;
        if (Math.Abs(scale - _lastDpiScale) < 0.001) return;
        _lastDpiScale = scale;
        ApplyViewportSize();
        if (_autoFit) _viewport.Fit();
        else _viewport.ClampOffsets();
        UpdateZoomChrome();
        PreviewCanvas.Invalidate();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyViewportSize();
        if (_autoFit) _viewport.Fit();
        else _viewport.ClampOffsets();
        UpdateZoomChrome();
        PreviewCanvas.Invalidate();
    }

    private async Task LoadAsync(string path, bool resetView)
    {
        var generation = ++_generation;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ReleaseImageResources();
        _loadedPath = path;
        _crossfade = 1;
        SetBlank(false);
        if (resetView) _autoFit = true;

        try
        {
            SetStatus("Reading image…", busy: true, error: false);

            var (width, height) = await SnipPreviewService.ReadSizeAsync(path, ct).ConfigureAwait(true);
            if (generation != _generation) return;

            if (width <= 0 || height <= 0)
            {
                var reason = SnipPreviewService.Exists(path) ? "This image could not be read." : "File missing";
                SetStatus(reason, busy: false, error: true);
                LoadFailed?.Invoke(this, reason);
                return;
            }

            var source = SnipPreviewPolicy.ChooseSource(width, height);
            _viewport.SetImage(width, height);
            ApplyViewportSize();
            if (_autoFit) _viewport.Fit();
            else _viewport.ClampOffsets();
            UpdateZoomChrome();
            PreviewCanvas.Invalidate();

            SetStatus($"{SnipPreviewPolicy.PlaceholderTier} px preview…", busy: true, error: false);
            var placeholder = await SnipPreviewService.LoadPlaceholderAsync(path, ct).ConfigureAwait(true);
            if (generation != _generation) return;

            if (placeholder is not null)
            {
                _placeholderPixels = placeholder.Bgra;
                _placeholderWidth = placeholder.Width;
                _placeholderHeight = placeholder.Height;
                _placeholderTier = placeholder.Tier;
                _blank = placeholder.LooksBlank;
                SetBlank(_blank);
                EnsurePlaceholderBitmap();
                PreviewCanvas.Invalidate();
            }

            if (source == SnipPreviewSource.VirtualBitmap)
            {
                SetStatus($"{width} × {height} • loading regions on demand…", busy: true, error: false);
                await EnsureVirtualBitmapAsync(path, ct).ConfigureAwait(true);
                if (generation != _generation) return;
            }
            else
            {
                SetStatus("Loading full resolution…", busy: true, error: false);
                var full = await SnipPreviewService.LoadFullPixelsAsync(path, ct).ConfigureAwait(true);
                if (generation != _generation) return;
                if (full is not null && full.Width > 0 && full.Height > 0)
                {
                    _fullPixels = full.Bgra;
                    _fullWidth = full.Width;
                    _fullHeight = full.Height;
                    EnsureFullBitmap();
                }
            }

            if (generation != _generation) return;

            if (HasFullSource)
            {
                StartCrossfade();
                SetStatus(_blank ? "Full resolution • image looks blank" : "Full resolution (100% = 1 image pixel : 1 screen pixel)", busy: false, error: false);
                ImageLoaded?.Invoke(this, EventArgs.Empty);
            }
            else if (_placeholderPixels is not null)
            {
                SetStatus($"{_placeholderTier} px preview • full resolution unavailable", busy: false, error: false);
            }
            else
            {
                var reason = SnipThumbnailService.GetLastError(path)
                    ?? (SnipPreviewService.Exists(path) ? "The preview could not be decoded." : "File missing");
                SetStatus(reason, busy: false, error: true);
                LoadFailed?.Invoke(this, reason);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            SetStatus(ex.Message, busy: false, error: true);
            LoadFailed?.Invoke(this, ex.Message);
        }
    }

    private void PreviewCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        ReleaseDeviceResources();
        var path = _loadedPath;
        if (!string.IsNullOrEmpty(path) && IsLoaded) _ = LoadAsync(path, resetView: false);
    }

    private void EnsurePlaceholderBitmap()
    {
        if (_placeholderPixels is null || PreviewCanvas is null) return;
        if (_placeholderBitmap is not null) return;
        if (_placeholderWidth <= 0 || _placeholderHeight <= 0) return;
        if (_placeholderPixels.Length < _placeholderWidth * _placeholderHeight * 4) return;

        try
        {
            ReleaseBitmap(ref _placeholderBitmap);
            _placeholderBitmap = CanvasBitmap.CreateFromBytes(
                PreviewCanvas, _placeholderPixels,
                _placeholderWidth, _placeholderHeight,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        }
        catch { _placeholderBitmap = null; }
    }

    private void EnsureFullBitmap()
    {
        if (_fullPixels is null || PreviewCanvas is null) return;
        if (_fullWidth <= 0 || _fullHeight <= 0) return;
        if (_fullPixels.Length < _fullWidth * _fullHeight * 4) return;

        try
        {
            ReleaseBitmap(ref _fullBitmap);
            _fullBitmap = CanvasBitmap.CreateFromBytes(
                PreviewCanvas, _fullPixels,
                _fullWidth, _fullHeight,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        }
        catch { _fullBitmap = null; }
    }

    private async Task EnsureVirtualBitmapAsync(string path, CancellationToken ct)
    {
        if (PreviewCanvas is null) return;
        try
        {
            ReleaseVirtualBitmap();
            var bitmap = await CanvasVirtualBitmap.LoadAsync(PreviewCanvas, path).AsTask(ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }
            _virtualBitmap = bitmap;

            var size = bitmap.SizeInPixels;
            if ((int)size.Width != _viewport.ImagePixelWidth || (int)size.Height != _viewport.ImagePixelHeight)
            {
                _viewport.SetImage((int)size.Width, (int)size.Height);
                ApplyViewportSize();
                if (_autoFit) _viewport.Fit(); else _viewport.ClampOffsets();
                UpdateZoomChrome();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            ReleaseVirtualBitmap();
        }
    }

    private void ReleaseDeviceResources()
    {
        ReleaseBitmap(ref _placeholderBitmap);
        ReleaseBitmap(ref _fullBitmap);
        ReleaseVirtualBitmap();
    }

    private void ReleaseImageResources()
    {
        ReleaseDeviceResources();
        _placeholderPixels = null;
        _placeholderWidth = _placeholderHeight = 0;
        _placeholderTier = 0;
        _fullPixels = null;
        _fullWidth = _fullHeight = 0;
        _dragging = false;
    }

    private static void ReleaseBitmap(ref CanvasBitmap? bitmap)
    {
        try { bitmap?.Dispose(); } catch { }
        bitmap = null;
    }

    private void ReleaseVirtualBitmap()
    {
        try { _virtualBitmap?.Dispose(); } catch { }
        _virtualBitmap = null;
    }

    private void PreviewCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!_viewport.HasImage) return;

        var session = args.DrawingSession;
        var interpolation = MapInterpolation(_viewport.Interpolation);
        var hasFull = HasFullSource;

        if (_placeholderBitmap is not null && (!hasFull || _crossfade < 1))
        {
            DrawVisible(session, _placeholderBitmap, 1f,
                MapInterpolation(SnipPreviewPolicy.PlaceholderInterpolation(_viewport.Zoom)));
        }

        if (hasFull)
        {
            if (_virtualBitmap is not null) DrawVisible(session, _virtualBitmap, (float)_crossfade, interpolation);
            else if (_fullBitmap is not null) DrawVisible(session, _fullBitmap, (float)_crossfade, interpolation);
        }
    }

    private void DrawVisible(CanvasDrawingSession session, ICanvasImage image, float opacity,
        CanvasImageInterpolation interpolation)
    {
        var (sourceX, sourceY, sourceWidth, sourceHeight) = _viewport.VisibleSourceRectPx();
        if (sourceWidth <= 0 || sourceHeight <= 0) return;

        var (destX, destY, destWidth, destHeight) =
            _viewport.ImageRectToViewportDip(sourceX, sourceY, sourceWidth, sourceHeight);
        if (destWidth <= 0 || destHeight <= 0) return;

        session.DrawImage(
            image,
            new Rect(destX, destY, destWidth, destHeight),
            new Rect(sourceX, sourceY, sourceWidth, sourceHeight),
            opacity,
            interpolation);
    }

    private static CanvasImageInterpolation MapInterpolation(SnipPreviewInterpolation interpolation) => interpolation switch
    {
        SnipPreviewInterpolation.NearestNeighbor => CanvasImageInterpolation.NearestNeighbor,
        SnipPreviewInterpolation.Linear => CanvasImageInterpolation.Linear,
        SnipPreviewInterpolation.Cubic => CanvasImageInterpolation.Cubic,
        _ => CanvasImageInterpolation.MultiSampleLinear,
    };

    private void StartCrossfade()
    {
        if (_placeholderBitmap is null || !HasFullSource)
        {
            _crossfade = 1;
            PreviewCanvas.Invalidate();
            return;
        }

        _crossfade = 0;
        _fadeClock.Restart();
        _fadeTimer ??= CreateFadeTimer();
        _fadeTimer.Start();
        PreviewCanvas.Invalidate();
    }

    private DispatcherQueueTimer CreateFadeTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            var progress = _fadeClock.Elapsed.TotalMilliseconds / CrossfadeMilliseconds;
            _crossfade = Math.Clamp(progress, 0, 1);
            PreviewCanvas.Invalidate();
            if (_crossfade >= 1)
            {
                _fadeClock.Stop();
                _fadeTimer?.Stop();
            }
        };
        return timer;
    }

    private bool HasFullSource => _virtualBitmap is not null || _fullBitmap is not null;

    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var controlPressed = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0;
        if (!WheelZoomEnabled && !controlPressed) return;

        var point = e.GetCurrentPoint(Root);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0 || !_viewport.HasImage) return;

        _autoFit = false;
        _viewport.ZoomAt(point.Position.X, point.Position.Y, Math.Pow(WheelZoomStep, delta / 120.0));
        UpdateZoomChrome();
        PreviewCanvas.Invalidate();
        e.Handled = true;
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsFromButton(e.OriginalSource)) return;

        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed || !_viewport.HasImage) return;

        _dragging = true;
        _dragOrigin = point.Position;
        Root.CapturePointer(e.Pointer);
        SetCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var position = e.GetCurrentPoint(Root).Position;

        if (!_viewport.IsFullyVisible)
        {
            _autoFit = false;
            _viewport.PanBy(position.X - _dragOrigin.X, position.Y - _dragOrigin.Y);
            PreviewCanvas.Invalidate();
        }
        _dragOrigin = position;
        e.Handled = true;
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);
        SetCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        SetCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    }

    private void Root_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        SetCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsFromButton(e.OriginalSource)) return;

        if (DoubleTapTogglesZoom)
        {
            if (_viewport.IsPixelPerfect) ZoomToFit();
            else ZoomToActualSize();
            e.Handled = true;
            return;
        }
        PreviewActivated?.Invoke(this, EventArgs.Empty);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => ZoomToFit();
    private void ZoomActual_Click(object sender, RoutedEventArgs e) => ZoomToActualSize();

    private static bool IsFromButton(object? originalSource)
    {
        var node = originalSource as DependencyObject;
        while (node is not null)
        {
            if (node is Button) return true;
            try { node = VisualTreeHelper.GetParent(node); }
            catch { return false; }
        }
        return false;
    }

    private void SetCursor(Microsoft.UI.Input.InputSystemCursorShape shape)
    {
        try
        {
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(shape);
        }
        catch { }
    }

    private double CurrentDpiScale
    {
        get
        {
            try
            {
                if (PreviewCanvas is { Dpi: > 0 }) return PreviewCanvas.Dpi / 96.0;
            }
            catch { }
            return XamlRoot?.RasterizationScale ?? 1.0;
        }
    }

    private void SyncViewport()
    {
        ApplyViewportSize();
        _viewport.SetRasterizationScale(CurrentDpiScale);
    }

    private void ApplyViewportSize() =>
        _viewport.SetViewport(ActualWidth, ActualHeight);

    private void UpdateZoomChrome() => ZoomLabel.Text = _viewport.ZoomLabel;

    private void SetBlank(bool blank)
    {
        _blank = blank;
        BlankBadge.Visibility = blank ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text, bool busy, bool error)
    {
        StatusLabel.Text = text;
        StatusBadge.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        var brush = error ? ThemeBrush("SystemFillColorCriticalBrush") : ThemeBrush("TextFillColorSecondaryBrush");
        if (brush is not null) StatusLabel.Foreground = brush;
    }

    private static Brush? ThemeBrush(string key)
    {
        try
        {
            return Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
        }
        catch
        {
            return null;
        }
    }
}
