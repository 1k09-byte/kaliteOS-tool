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

/// <summary>
/// The large Snip preview: one Win2D surface used by the gallery details panel and the full
/// viewer, so the two surfaces can never disagree about a snip.
///
/// Progressiveness: the 512 px tier (already in the thumbnail cache) is drawn first, then the
/// full-resolution source crossfades in over it. Full resolution means either
///   - a one-shot decode into a CanvasBitmap (normal snips), or
///   - CanvasVirtualBitmap for huge snips, where only the regions behind the visible source
///     rectangle are ever realized - a 20000 x 20000 capture never has to be resident.
///
/// Zoom/pan lives in <see cref="SnipPreviewViewport"/> (pure, unit-tested). Zoom 1.0 is 100%:
/// one image pixel per PHYSICAL device pixel, at whatever the monitor scale is. Interpolation
/// follows the zoom level (averaged when minified, crisp when magnified).
/// </summary>
public sealed partial class SnipPreviewView : UserControl
{
    /// <summary>Duration of the placeholder -> full-resolution crossfade.</summary>
    private const double CrossfadeMilliseconds = 220;

    /// <summary>Wheel notch factor (per 120 units of wheel delta).</summary>
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

    // ------------------------------------------------------------ public surface

    /// <summary>Raised on double-tap when <see cref="DoubleTapTogglesZoom"/> is false: the host
    /// (details panel) uses it to detach the snip into the viewer window.</summary>
    public event EventHandler? PreviewActivated;

    /// <summary>Raised when the preview reached full resolution (or an on-demand source).</summary>
    public event EventHandler? ImageLoaded;

    /// <summary>Raised with a user-facing reason when nothing could be shown.</summary>
    public event EventHandler<string>? LoadFailed;

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(SnipPreviewView),
        new PropertyMetadata(string.Empty, OnSourcePathChanged));

    public static readonly DependencyProperty WheelZoomEnabledProperty = DependencyProperty.Register(
        nameof(WheelZoomEnabled), typeof(bool), typeof(SnipPreviewView), new PropertyMetadata(false));

    /// <summary>Path of the snip to preview. Setting it (re)starts the progressive load.</summary>
    public string SourcePath
    {
        get => (string)(GetValue(SourcePathProperty) ?? string.Empty);
        set => SetValue(SourcePathProperty, value);
    }

    /// <summary>Plain wheel zooms when true (the viewer). Ctrl+wheel always zooms, so the details
    /// panel keeps scrolling normally with a plain wheel. </summary>
    public bool WheelZoomEnabled
    {
        get => (bool)GetValue(WheelZoomEnabledProperty);
        set => SetValue(WheelZoomEnabledProperty, value);
    }

    /// <summary>Viewer behaviour: double-tap toggles Fit / 100% instead of raising
    /// <see cref="PreviewActivated"/>.</summary>
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

    // ------------------------------------------------------------ lifecycle

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
            // x:Bind sets the path before the element is in the tree: defer to Loaded.
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

    // ------------------------------------------------------------ loading

    /// <summary>
    /// The progressive load: size -> 512 px tier (drawn immediately) -> full resolution
    /// (crossfaded in). Every stage is cancelled by the next request, so changing the selected
    /// snip quickly can never paint the wrong image.
    /// </summary>
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

            // 1st pass: the tier cache (no decode for a snip the gallery already showed).
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

            // 2nd pass: full resolution.
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
            // Superseded by a newer selection: not an error.
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            SetStatus(ex.Message, busy: false, error: true);
            LoadFailed?.Invoke(this, ex.Message);
        }
    }

    // ------------------------------------------------------------ GPU resources

    private void PreviewCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // First creation and device loss both arrive here: throw away the stale device resources
        // and rebuild them from the path/pixels we already know about.
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

    /// <summary>
    /// Huge images never get decoded into memory: CanvasVirtualBitmap realizes only the regions
    /// behind the source rectangle we actually draw.
    /// </summary>
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

            // The bitmap is the authority on its own pixel size (EXIF rotation, codecs): if it
            // disagrees with the header the viewport used, follow the bitmap so the source and
            // destination rectangles stay in step.
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
            // Fall back to the placeholder only: the caller reports "full resolution unavailable".
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

    // ------------------------------------------------------------ drawing

    private void PreviewCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!_viewport.HasImage) return;

        var session = args.DrawingSession;
        var interpolation = MapInterpolation(_viewport.Interpolation);
        var hasFull = HasFullSource;

        // Placeholder first (static fallback + the layer being faded over), then the
        // full-resolution source on top with the crossfade opacity.
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

    /// <summary>
    /// Draws only the part of the image that is on screen, mapped from image pixels to viewport
    /// DIPs. For a virtual bitmap this is what keeps the realized region count proportional to the
    /// window, not to the image.
    /// </summary>
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

    // ------------------------------------------------------------ input

    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var controlPressed = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0;
        if (!WheelZoomEnabled && !controlPressed) return;   // let the details panel scroll

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
        // Pointer events bubble out of the zoom buttons too: capturing the pointer for a pan here
        // would steal the button's own capture and swallow its click.
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

        // Dragging only means panning once the image is larger than the viewport; a fitting
        // image stays centered.
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

    /// <summary>True when the event came from a button (the zoom chrome), not the image surface.</summary>
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

    // ------------------------------------------------------------ plumbing

    /// <summary>
    /// The DIP -> device-pixel factor the Win2D surface is actually rendered at. Queried from the
    /// control itself (Win2D sizes its drawing resource from the environment DPI), falling back to
    /// the XamlRoot scale before the surface exists.
    /// </summary>
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
