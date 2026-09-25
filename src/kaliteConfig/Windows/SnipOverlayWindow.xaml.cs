using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinRT.Interop;
using kaliteConfig.Services;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace kaliteConfig.Views;

public sealed partial class SnipOverlayWindow : Window
{
    private AppWindow _appWindow;
    private byte[]? _bgraPixels;
    private int _imgWidth;
    private int _imgHeight;

    /// <summary>Exe name focused when capture fired (set by SnipService for the Source column).</summary>
    public string SourceApp { get; set; } = "";

    // Display scale factor. The captured bitmap is in physical pixels, but the
    // CanvasControl defaults to 96 DPI (logical units). We set the canvas DPI to
    // 96 * scale so 1 drawing unit == 1 physical pixel (crisp preview, and
    // selection coordinates line up with the bitmap exactly). Pointer positions
    // arrive in DIPs, so they are multiplied by _dpiScale before use.
    private double _dpiScale = 1.0;
    private bool _dpiScaleApplied;

    private bool _isDragging;
    private Point _startPoint;
    private Point _endPoint;

    private enum ActiveTool { Select, Arrow, Line, Rectangle, Ellipse, Ink, Highlight, Text, Number, Blur, Spotlight, Sticker }
    private ActiveTool _currentTool = ActiveTool.Select;
    private int _nextNumber = 1;

    private List<Models.SnipObject> _annotations = new();
    private List<Models.SnipObject> _redoStack = new();
    private Models.SnipObject? _currentAnnotation;
    private Point _drawStartPoint;
    private Models.SnipObject? _movingAnnotation;
    private Point _moveLast;
    private (byte[] Bgra, int W, int H)? _activeSticker;

    /// <summary>Topmost-first annotation hit test (last drawn = top).</summary>
    private Models.SnipObject? AnnotationAt(Point px)
    {
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
            try { if (_annotations[i].HitTest(px)) return _annotations[i]; } catch { }
        }
        return null;
    }

    private void ClearAnnotationSelection()
    {
        foreach (var a in _annotations) a.IsSelected = false;
    }

    private void DeleteSelectedAnnotations()
    {
        bool any = false;
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
            if (_annotations[i].IsSelected)
            {
                _redoStack.Add(_annotations[i]);
                _annotations.RemoveAt(i);
                any = true;
            }
        }
        if (any) { DrawCanvas.Invalidate(); UpdateHud("ann-delete"); }
    }

    private void DuplicateSelectedAnnotations()
    {
        var copies = new System.Collections.Generic.List<Models.SnipObject>();
        foreach (var a in _annotations)
        {
            if (!a.IsSelected) continue;
            var c = CloneAnnotation(a);
            if (c != null) { c.MoveBy(12, 12); copies.Add(c); }
        }
        if (copies.Count > 0)
        {
            ClearAnnotationSelection();
            foreach (var c in copies) { c.IsSelected = true; _annotations.Add(c); }
            _redoStack.Clear();
            DrawCanvas.Invalidate();
            UpdateHud("ann-duplicate");
        }
    }

    private static Models.SnipObject? CloneAnnotation(Models.SnipObject a)
    {
        switch (a)
        {
            case Models.SnipArrow v: return new Models.SnipArrow { Start = v.Start, End = v.End, Color = v.Color, StrokeThickness = v.StrokeThickness };
            case Models.SnipLine v: return new Models.SnipLine { Start = v.Start, End = v.End, Color = v.Color, StrokeThickness = v.StrokeThickness };
            case Models.SnipRectangle v: return new Models.SnipRectangle { Bounds = v.Bounds, Color = v.Color, StrokeThickness = v.StrokeThickness };
            case Models.SnipEllipse v: return new Models.SnipEllipse { Bounds = v.Bounds, Color = v.Color, StrokeThickness = v.StrokeThickness, Fill = v.Fill };
            case Models.SnipHighlight v: { var c = new Models.SnipHighlight { Color = v.Color, StrokeThickness = v.StrokeThickness }; foreach (var p in v.Points) c.Points.Add(p); return c; }
            case Models.SnipInk v: { var c = new Models.SnipInk { Color = v.Color, StrokeThickness = v.StrokeThickness }; foreach (var p in v.Points) c.Points.Add(p); return c; }
            case Models.SnipText v: return new Models.SnipText { Position = v.Position, TextValue = v.TextValue, Color = v.Color, FontSize = v.FontSize };
            case Models.SnipNumber v: return new Models.SnipNumber { Center = v.Center, Number = v.Number, Color = v.Color, StrokeThickness = v.StrokeThickness };
            case Models.SnipRedactionBox v: return new Models.SnipRedactionBox { Bounds = v.Bounds, PixelateMode = v.PixelateMode };
            case Models.SnipSpotlight v: return new Models.SnipSpotlight { Bounds = v.Bounds, Color = v.Color, StrokeThickness = v.StrokeThickness, ScreenWidth = v.ScreenWidth, ScreenHeight = v.ScreenHeight };
            case Models.SnipSticker v: return new Models.SnipSticker { Bgra = v.Bgra, PixelWidth = v.PixelWidth, PixelHeight = v.PixelHeight, Bounds = v.Bounds };
            default: return null;
        }
    }

    private void MoveSelectedZ(int dir)
    {
        int idx = _annotations.FindIndex(a => a.IsSelected);
        if (idx < 0) return;
        int to = Math.Max(0, Math.Min(_annotations.Count - 1, idx + dir));
        if (to == idx) return;
        var item = _annotations[idx];
        _annotations.RemoveAt(idx);
        _annotations.Insert(to, item);
        DrawCanvas.Invalidate();
        UpdateHud("ann-zorder");
    }

    private Windows.UI.Color _currentColor = Microsoft.UI.Colors.Red;
    private float _currentStrokeThickness = 4f;

    // Pending free-typing text annotation
    private TextBox? _textEditor;

    private static readonly Windows.UI.Color[] PresetColors =
    {
        Microsoft.UI.Colors.Red,
        Microsoft.UI.Colors.Orange,
        Microsoft.UI.Colors.Yellow,
        Microsoft.UI.Colors.LimeGreen,
        Microsoft.UI.Colors.Cyan,
        Microsoft.UI.Colors.Blue,
        Microsoft.UI.Colors.Magenta,
        Microsoft.UI.Colors.White,
    };

    private enum DragMode { None, NewSelection, TopLeft, TopCenter, TopRight, MiddleLeft, MiddleRight, BottomLeft, BottomCenter, BottomRight, RootPan }
    private DragMode _dragMode = DragMode.None;
    private Point _panAnchor;
    private Point _startPointAnchor;
    private Point _endPointAnchor;

    // Explicit capture state machine (Idle -> Selecting -> Selected -> Closed).
    // Fresh windows are used per capture; ResetState additionally guarantees the
    // 20th snip behaves like the first even if a window is ever reused.
    private Services.SnipCaptureState _state = Services.SnipCaptureState.Idle;
    private Point _pressPx;       // press position, bitmap pixels
    private Point _lastCursorPx;  // last known cursor, bitmap pixels
    private int _monX, _monY, _monW, _monH; // captured monitor rect, screen pixels
    private bool _hudVisible;
    private DateTime _lastHudLog = DateTime.MinValue;
    private readonly Services.SnipRegionLogic.ModifierHoldTracker _mods = new();

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out WinPoint lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int X; public int Y; }

    private static void OverlayLog(string message)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "snip-overlay.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch { }
    }

    private static bool KeyDownLive(Windows.System.VirtualKey key) =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down)
        == Windows.UI.Core.CoreVirtualKeyStates.Down;

    public SnipOverlayWindow()
    {
        this.InitializeComponent();

        _appWindow = this.AppWindow;

        // Borderless + always-on-top instead of ExtendsContentIntoTitleBar + FullScreen.
        // Extending content into the title bar makes Windows install a DRAG REGION across the
        // top of the window (documented behaviour), which swallows the pointer and starts a
        // window drag instead of a selection box. A frame-less window has no caption, no drag
        // region and no inset, so its client area is exactly the captured monitor rectangle.
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        BuildSwatches();
        UpdateInstantModeVisual();
    }

    /// <summary>Converts a DIP-space pointer position into bitmap pixel space.</summary>
    private Point ToPixels(Point p) => new Point(p.X * _dpiScale, p.Y * _dpiScale);

    private void ApplyDpiScale()
    {
        if (_dpiScaleApplied) return;
        try
        {
            if (RootGrid is null || RootGrid.XamlRoot is null) return; // content not loaded yet
            _dpiScale = RootGrid.XamlRoot.RasterizationScale;
            if (_dpiScale > 0)
            {
                _dpiScaleApplied = true;
            }
        }
        catch { }
        if (_dpiScale <= 0) _dpiScale = 1.0;
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDpiScale();
        _mods.CapturePreheld(
            KeyDownLive(Windows.System.VirtualKey.Shift),
            KeyDownLive(Windows.System.VirtualKey.Control),
            KeyDownLive(Windows.System.VirtualKey.Menu));
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // Keep this window out of any capture (multi-overlay safety). The frozen
            // frame was grabbed before we existed, so this cannot blank our own image.
            if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
                OverlayLog("WARN SetWindowDisplayAffinity failed");
            bool fg = GetForegroundWindow() == hwnd;
            OverlayLog($"show mon=({_monX},{_monY} {_monW}x{_monH}) dpi={_dpiScale} fg={fg} " +
                $"preheld(shift,ctrl,alt)=({_mods.ShiftPreheld},{_mods.CtrlPreheld},{_mods.AltPreheld})");
            if (!fg) this.Activate();
        }
        catch (Exception ex) { OverlayLog("show ERR " + ex.Message); }
        DrawCanvas.Invalidate();
        RootGrid.Focus(FocusState.Programmatic);
        ApplySavedSettings();
        UpdateFormatRow();
        SetOverlayCursor(Microsoft.UI.Input.InputSystemCursorShape.Cross);
        UpdateHud("show");
    }

    /// <summary>Full reset: selection, drag, annotations, toolbar, hover. Called on
    /// every show so reused windows cannot leak previous-capture state.</summary>
    private void ResetState()
    {
        _state = Services.SnipCaptureState.Idle;
        _startPoint = new Point(0, 0);
        _endPoint = new Point(0, 0);
        _pressPx = new Point(0, 0);
        _lastCursorPx = new Point(0, 0);
        _dragMode = DragMode.None;
        _isDragging = false;
        _annotations.Clear();
        _redoStack.Clear();
        _currentAnnotation = null;
        _nextNumber = 1;
        HideTextEditor();
        if (SnipToolbar != null) SnipToolbar.Visibility = Visibility.Collapsed;
        if (ToolbarHost != null) ToolbarHost.Visibility = Visibility.Collapsed;
    }

    // WinUI 3 exposes the cursor only as UIElement.ProtectedCursor (protected).
    // Without a subclassed root element, reflection is the documented fallback
    // (WindowsAppSDK discussion #1816). Crosshair while Idle/Selecting, sizing
    // cursors on handles - all guarded so a failure just keeps the arrow.
    private void SetOverlayCursor(Microsoft.UI.Input.InputSystemCursorShape shape)
    {
        try
        {
            var cursor = Microsoft.UI.Input.InputSystemCursor.Create(shape);
            typeof(Microsoft.UI.Xaml.UIElement).InvokeMember("ProtectedCursor",
                System.Reflection.BindingFlags.SetProperty
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic,
                null, RootGrid, new object[] { cursor });
        }
        catch (Exception ex) { OverlayLog("cursor WARN " + ex.GetType().Name); }
    }

    private void StickerFlyout_Opened(object sender, object e)
    {
        RefreshStickerGrid();
    }

    private void RefreshStickerGrid()
    {
        try
        {
            StickerGridView.ItemsSource = Services.SnipStickerService.GetAllStickers()
                .Select(s => s.FilePath)
                .ToList();
        }
        catch (Exception ex)
        {
            OverlayLog("Error loading stickers: " + ex.Message);
        }
    }

    private async void BtnImportSticker_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                // Safe unique names via the shared service (same-second imports no longer overwrite).
                await Services.SnipStickerService.ImportStickersAsync(new[] { file.Path });
                RefreshStickerGrid();
            }
        }
        catch (Exception ex)
        {
            OverlayLog("Import sticker error: " + ex.Message);
        }
    }

    private async void StickerGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string path)
        {
            try
            {
                // Decode to raw pixels (not a device-bound bitmap): the sticker must draw
                // on both the preview device and the export render target.
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                var data = await decoder.GetPixelDataAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    new Windows.Graphics.Imaging.BitmapTransform(),
                    Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                    Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
                _activeSticker = (data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
                OverlayLog($"sticker selected {System.IO.Path.GetFileName(path)} {_activeSticker.Value.W}x{_activeSticker.Value.H}");

                // Drop the sticker straight onto the shot (selection center, selected),
                // so it can be dragged into place immediately - no extra click needed.
                double scx = (_startPoint.X + _endPoint.X) / 2, scy = (_startPoint.Y + _endPoint.Y) / 2;
                var stamp = CreateStickerAt(new Point(scx, scy));
                if (stamp != null)
                {
                    ClearAnnotationSelection();
                    stamp.IsSelected = true;
                    _annotations.Add(stamp);
                    _redoStack.Clear();
                    DrawCanvas.Invalidate();
                    OverlayLog($"sticker stamped total={_annotations.Count}");
                }
                // Clear selection states of other toggle buttons
                foreach (var btn in ToolButtons)
                {
                    btn.IsChecked = false;
                }
                
                _currentTool = ActiveTool.Sticker;
                BtnToolSticker.Flyout.Hide();
            }
            catch (Exception ex)
            {
                OverlayLog("Error loading sticker bitmap: " + ex.Message);
            }
        }
    }

    private void BuildSwatches()
    {
        SwatchPanel.Children.Clear();
        foreach (var color in PresetColors)
        {
            var swatch = new Button
            {
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                Background = new SolidColorBrush(color),
                Tag = color,
            };
            ToolTipService.SetToolTip(swatch, $"Color {color.ToString()}");
            AutomationProperties.SetName(swatch, $"Color {color}");
            swatch.Click += (_, _) => { _currentColor = color; ColorPickerTool.Color = color; };
            SwatchPanel.Children.Add(swatch);
        }
    }

    private void ApplySavedSettings()
    {
        var settings = SnipSettingsService.Load();

        if (!string.IsNullOrEmpty(settings.ActiveTool))
        {
            var toolName = settings.ActiveTool;
            // Legacy button names from the old toolbar map onto the new tools.
            toolName = toolName switch
            {
                "BtnToolRect" => "Select",
                "BtnToolArrow" => "Arrow",
                "BtnToolBox" => "Rectangle",
                "BtnToolInk" => "Ink",
                "BtnToolBlur" => "Blur",
                _ => toolName,
            };
            var btn = ToolButtons.FirstOrDefault(b => string.Equals((string)b.Tag, toolName, StringComparison.OrdinalIgnoreCase));
            if (btn != null) BtnTool_Click(btn, new RoutedEventArgs());
        }

        _currentStrokeThickness = settings.Thickness;
        SliderThickness.Value = settings.Thickness;
        UpdatePreviewDot();

        var parsed = TryParseColor(settings.ColorHex);
        if (parsed.HasValue)
        {
            _currentColor = parsed.Value;
            ColorPickerTool.Color = parsed.Value;
        }

        BtnInstantMode.IsChecked = settings.InstantMode;
        UpdateInstantModeVisual();
    }

    private IEnumerable<ToggleButton> ToolButtons
    {
        get
        {
            yield return BtnToolSelect;
            yield return BtnToolArrow;
            yield return BtnToolLine;
            yield return BtnToolRect;
            yield return BtnToolEllipse;
            yield return BtnToolInk;
            yield return BtnToolHighlight;
            yield return BtnToolText;
            yield return BtnToolNumber;
            yield return BtnToolBlur;
            yield return BtnToolSpotlight;
        }
    }

    private static Windows.UI.Color? TryParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.Trim().TrimStart('#');
        try
        {
            if (hex.Length == 6)
                return Windows.UI.Color.FromArgb(255,
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            if (hex.Length == 8)
                return Windows.UI.Color.FromArgb(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16),
                    Convert.ToByte(hex.Substring(6, 2), 16));
        }
        catch { }
        return null;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F12)
        {
            _hudVisible = !_hudVisible;
            if (HudPanel != null) HudPanel.Visibility = _hudVisible ? Visibility.Visible : Visibility.Collapsed;
            UpdateHud("F12");
            OverlayLog("HUD " + (_hudVisible ? "on" : "off"));
            e.Handled = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _state = Services.SnipCaptureState.Closed;
            OverlayLog("key Esc -> close");
            ((App)Microsoft.UI.Xaml.Application.Current).Sniper.CloseOverlays();
            return;
        }
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            DeleteSelectedAnnotations();
            e.Handled = true;
            return;
        }

        var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        bool ctrl = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (ctrl)
        {
            if (e.Key == Windows.System.VirtualKey.S) { BtnSave_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
            if (e.Key == Windows.System.VirtualKey.C) { BtnCopy_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
            if (e.Key == Windows.System.VirtualKey.Z) { BtnUndo_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
            if (e.Key == Windows.System.VirtualKey.Y) { BtnRedo_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
            if (e.Key == Windows.System.VirtualKey.D) { DuplicateSelectedAnnotations(); e.Handled = true; return; }
        }
        else
        {
            // Single-key tool shortcuts
            ActiveTool? tool = e.Key switch
            {
                Windows.System.VirtualKey.V => ActiveTool.Select,
                Windows.System.VirtualKey.A => ActiveTool.Arrow,
                Windows.System.VirtualKey.L => ActiveTool.Line,
                Windows.System.VirtualKey.R => ActiveTool.Rectangle,
                Windows.System.VirtualKey.O => ActiveTool.Ellipse,
                Windows.System.VirtualKey.P => ActiveTool.Ink,
                Windows.System.VirtualKey.H => ActiveTool.Highlight,
                Windows.System.VirtualKey.T => ActiveTool.Text,
                Windows.System.VirtualKey.N => ActiveTool.Number,
                Windows.System.VirtualKey.B => ActiveTool.Blur,
                _ => null,
            };
            if (e.Key == Windows.System.VirtualKey.K)
            {
                try { BtnToolSticker.Flyout.ShowAt(BtnToolSticker); } catch { }
                e.Handled = true;
                return;
            }
            if (tool.HasValue)
            {
                var btn = ToolButtons.First(b => (ActiveTool)Enum.Parse(typeof(ActiveTool), (string)b.Tag) == tool.Value);
                BtnTool_Click(btn, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                BtnCopy_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            // [ and ] move the selected annotation back / forward in z-order.
            if (e.Key == (Windows.System.VirtualKey)219) { MoveSelectedZ(-1); e.Handled = true; return; }
            if (e.Key == (Windows.System.VirtualKey)221) { MoveSelectedZ(1); e.Handled = true; return; }
        }

        if (SnipToolbar.Visibility == Visibility.Visible)
        {
            double step = 1;
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            if ((state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down)
                step = 10;

            double dx = 0, dy = 0;
            if (e.Key == Windows.System.VirtualKey.Left) dx = -step;
            else if (e.Key == Windows.System.VirtualKey.Right) dx = step;
            else if (e.Key == Windows.System.VirtualKey.Up) dy = -step;
            else if (e.Key == Windows.System.VirtualKey.Down) dy = step;

            if (dx != 0 || dy != 0)
            {
                _startPoint.X += dx;
                _startPoint.Y += dy;
                _endPoint.X += dx;
                _endPoint.Y += dy;
                DrawCanvas.Invalidate();
                PositionToolbar();
            }
        }
    }

    private void DrawCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        ApplyDpiScale();
        args.DrawingSession.Units = Microsoft.Graphics.Canvas.CanvasUnits.Pixels;
        args.DrawingSession.Clear(Microsoft.UI.Colors.Transparent);
        if (_bgraPixels != null)
        {
            using var bmp = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(
                sender, _bgraPixels, _imgWidth, _imgHeight,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            args.DrawingSession.DrawImage(bmp, 0, 0);

            var dimColor = Microsoft.UI.ColorHelper.FromArgb(120, 0, 0, 0);
            float x = (float)Math.Min(_startPoint.X, _endPoint.X);
            float y = (float)Math.Min(_startPoint.Y, _endPoint.Y);
            float w = (float)Math.Abs(_startPoint.X - _endPoint.X);
            float h = (float)Math.Abs(_startPoint.Y - _endPoint.Y);

            if (w > 0 || h > 0)
            {
                args.DrawingSession.FillRectangle(0, 0, (float)_imgWidth, y, dimColor);
                args.DrawingSession.FillRectangle(0, y + h, (float)_imgWidth, (float)_imgHeight - (y + h), dimColor);
                args.DrawingSession.FillRectangle(0, y, x, h, dimColor);
                args.DrawingSession.FillRectangle(x + w, y, (float)_imgWidth - (x + w), h, dimColor);

                foreach (var ann in _annotations)
                {
                    if (ann is Models.SnipSpotlight sp) { sp.ScreenWidth = _imgWidth; sp.ScreenHeight = _imgHeight; }
                    // The frozen frame is the effect background: blur/pixelate needs it
                    // in the live preview too, otherwise redactions look missing.
                    ann.Draw(args.DrawingSession, bmp);
                    if (ann.IsSelected)
                    {
                        try
                        {
                            var b = ann.GetBounds();
                            args.DrawingSession.DrawRectangle((float)b.X - 3, (float)b.Y - 3,
                                (float)b.Width + 6, (float)b.Height + 6, Microsoft.UI.Colors.Yellow, 2);
                        }
                        catch { }
                    }
                }

                args.DrawingSession.DrawRectangle(x, y, w, h, Microsoft.UI.Colors.White, 2);

                if (_isDragging && _state == Services.SnipCaptureState.Selecting)
                {
                    // Live W x H readout near the cursor.
                    string dims = $"{w:0} × {h:0}";
                    double tx = Math.Max(4, Math.Min(_lastCursorPx.X + 16, _imgWidth - 110));
                    double ty = Math.Max(4, Math.Min(_lastCursorPx.Y + 16, _imgHeight - 30));
                    args.DrawingSession.FillRectangle((float)tx - 3, (float)ty - 2, dims.Length * 8 + 10, 22,
                        Microsoft.UI.ColorHelper.FromArgb(200, 0, 0, 0));
                    using var fmt = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontSize = 14 };
                    args.DrawingSession.DrawText(dims, (float)tx, (float)ty, Microsoft.UI.Colors.White, fmt);

                    // Magnifier: 3x zoom of the frozen frame around the cursor.
                    const float mag = 3f, src = 46f;
                    float sx = (float)Math.Max(0, Math.Min(_lastCursorPx.X - src / 2, _imgWidth - src));
                    float sy = (float)Math.Max(0, Math.Min(_lastCursorPx.Y - src / 2, _imgHeight - src));
                    float dw = src * mag;
                    float dx = (float)Math.Max(4, Math.Min(_lastCursorPx.X + 20, _imgWidth - dw - 4));
                    float dy = (float)Math.Max(4, Math.Min(_lastCursorPx.Y - dw - 28, _imgHeight - dw - 4));
                    args.DrawingSession.FillRectangle(dx, dy, dw, dw, Microsoft.UI.Colors.Black);
                    args.DrawingSession.DrawImage(bmp, new Rect(dx, dy, dw, dw), new Rect(sx, sy, src, src));
                    args.DrawingSession.DrawRectangle(dx, dy, dw, dw, Microsoft.UI.Colors.White, 2);
                    float cx = dx + dw / 2, cy = dy + dw / 2;
                    args.DrawingSession.DrawLine(cx - 8, cy, cx + 8, cy, Microsoft.UI.Colors.Red, 1);
                    args.DrawingSession.DrawLine(cx, cy - 8, cx, cy + 8, Microsoft.UI.Colors.Red, 1);
                    args.DrawingSession.DrawText($"{_lastCursorPx.X:0},{_lastCursorPx.Y:0}", dx + 4, dy + dw - 20,
                        Microsoft.UI.Colors.White, fmt);
                }

                if (!_isDragging)
                {
                    float r = 5f;
                    void DrawHandle(float hX, float hY)
                    {
                        args.DrawingSession.FillCircle(hX, hY, r, Microsoft.UI.Colors.White);
                        args.DrawingSession.DrawCircle(hX, hY, r, Microsoft.UI.Colors.Black, 1);
                    }
                    DrawHandle(x, y);
                    DrawHandle(x + w / 2, y);
                    DrawHandle(x + w, y);
                    DrawHandle(x, y + h / 2);
                    DrawHandle(x + w, y + h / 2);
                    DrawHandle(x, y + h);
                    DrawHandle(x + w / 2, y + h);
                    DrawHandle(x + w, y + h);

                    if (SnipToolbar.Visibility != Visibility.Visible)
                    {
                        SnipToolbar.Visibility = Visibility.Visible;
                        DispatcherQueue.TryEnqueue(() => PositionToolbar());
                    }
                }
            }
            else
            {
                args.DrawingSession.FillRectangle(0, 0, (float)_imgWidth, (float)_imgHeight, dimColor);
            }
        }
    }

    public void LoadBitmap(byte[] bgraPixels, int w, int h)
    {
        ResetState();
        ApplyDpiScale();
        _bgraPixels = bgraPixels;
        _imgWidth = w;
        _imgHeight = h;
        DrawCanvas.Invalidate();
    }

    private DragMode HitTest(Point pt)
    {
        float x = (float)Math.Min(_startPoint.X, _endPoint.X);
        float y = (float)Math.Min(_startPoint.Y, _endPoint.Y);
        float w = (float)Math.Abs(_startPoint.X - _endPoint.X);
        float h = (float)Math.Abs(_startPoint.Y - _endPoint.Y);
        float grab = (float)(12 * (_dpiScale <= 0 ? 1 : _dpiScale)); // 12 DIP grab radius, in bitmap pixels
        return Services.SnipRegionLogic.HitTestSelection(x, y, w, h, pt.X, pt.Y, grab) switch
        {
            Services.SnipHit.NewSelection => DragMode.NewSelection,
            Services.SnipHit.TopLeft => DragMode.TopLeft,
            Services.SnipHit.TopCenter => DragMode.TopCenter,
            Services.SnipHit.TopRight => DragMode.TopRight,
            Services.SnipHit.MiddleLeft => DragMode.MiddleLeft,
            Services.SnipHit.MiddleRight => DragMode.MiddleRight,
            Services.SnipHit.BottomLeft => DragMode.BottomLeft,
            Services.SnipHit.BottomCenter => DragMode.BottomCenter,
            Services.SnipHit.BottomRight => DragMode.BottomRight,
            _ => DragMode.RootPan,
        };
    }

    private Models.SnipObject? CreateAnnotation(Point pt)
    {
        switch (_currentTool)
        {
            case ActiveTool.Arrow: return new Models.SnipArrow { Start = pt, End = pt, Color = _currentColor, StrokeThickness = _currentStrokeThickness };
            case ActiveTool.Line: return new Models.SnipLine { Start = pt, End = pt, Color = _currentColor, StrokeThickness = _currentStrokeThickness };
            case ActiveTool.Rectangle: return new Models.SnipRectangle { Bounds = new Rect(pt.X, pt.Y, 0, 0), Color = _currentColor, StrokeThickness = _currentStrokeThickness };
            case ActiveTool.Ellipse: return new Models.SnipEllipse { Bounds = new Rect(pt.X, pt.Y, 0, 0), Color = _currentColor, StrokeThickness = _currentStrokeThickness, Fill = BtnFillToggle.IsChecked == true };
            case ActiveTool.Ink: { var a = new Models.SnipInk { Color = _currentColor, StrokeThickness = _currentStrokeThickness }; a.UpdateBounds(pt, pt); return a; }
            case ActiveTool.Highlight: { var a = new Models.SnipHighlight { StrokeThickness = _currentStrokeThickness }; a.Color = _currentColor; a.UpdateBounds(pt, pt); return a; }
            case ActiveTool.Text: return new Models.SnipText { Position = pt, Color = _currentColor, FontSize = Math.Max(14f, _currentStrokeThickness * 4) };
            case ActiveTool.Number: return new Models.SnipNumber { Center = pt, Number = _nextNumber, Color = _currentColor, StrokeThickness = _currentStrokeThickness };
            case ActiveTool.Blur: return new Models.SnipRedactionBox { Bounds = new Rect(pt.X, pt.Y, 0, 0) };
            case ActiveTool.Spotlight: return new Models.SnipSpotlight { Bounds = new Rect(pt.X, pt.Y, 0, 0), Color = _currentColor, StrokeThickness = _currentStrokeThickness, ScreenWidth = _imgWidth, ScreenHeight = _imgHeight };
            case ActiveTool.Sticker: return CreateStickerAt(pt);
            default: return null;
        }
    }

    /// <summary>Stamps a sticker at a usable default size (longest edge 160 px) centered
    /// on the given point and clamped into the selection, so a plain click still leaves
    /// a visible stamp. Dragging resizes from the press point via UpdateBounds.</summary>
    private Models.SnipSticker? CreateStickerAt(Point pt)
    {
        if (_activeSticker is not { } st) return null;
        var (bx, by, dw, dh) = DefaultStickerBounds(pt, st.W, st.H);
        return new Models.SnipSticker
        {
            Bgra = st.Bgra, PixelWidth = st.W, PixelHeight = st.H,
            Bounds = new Rect(bx, by, dw, dh),
        };
    }

    private (double X, double Y, double W, double H) DefaultStickerBounds(Point center, int natW, int natH)
    {
        double fit = Math.Min(1.0, 160.0 / Math.Max(1, Math.Max(natW, natH)));
        double dw = Math.Max(8, natW * fit), dh = Math.Max(8, natH * fit);
        double sx = Math.Min(_startPoint.X, _endPoint.X), sy = Math.Min(_startPoint.Y, _endPoint.Y);
        double sw = Math.Abs(_endPoint.X - _startPoint.X), sh = Math.Abs(_endPoint.Y - _startPoint.Y);
        dw = sw > 0 ? Math.Min(dw, sw) : dw;
        dh = sh > 0 ? Math.Min(dh, sh) : dh;
        double bx = sw > dw ? Math.Max(sx, Math.Min(center.X - dw / 2, sx + sw - dw)) : sx;
        double by = sh > dh ? Math.Max(sy, Math.Min(center.Y - dh / 2, sy + sh - dh)) : sy;
        return (bx, by, dw, dh);
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var raw = e.GetCurrentPoint(RootGrid);
        if (raw.Properties.IsRightButtonPressed)
        {
            _state = Services.SnipCaptureState.Closed;
            OverlayLog("press right-click -> close");
            this.Close();
            e.Handled = true;
            return;
        }
        var pt = ToPixels(raw.Position);
        _pressPx = pt;
        _lastCursorPx = pt;
        ObserveModifiersLive();
        SetOverlayCursor(Microsoft.UI.Input.InputSystemCursorShape.Cross);

        // Text annotation: click places an inline editor; the object is
        // committed when the user presses Enter.
        if (_currentTool == ActiveTool.Text && SnipToolbar.Visibility == Visibility.Visible)
        {
            BeginTextEditor(pt);
            return;
        }

        if (_currentTool != ActiveTool.Select && SnipToolbar.Visibility == Visibility.Visible)
        {
            // Sticker tool with nothing chosen yet: open the picker instead of stamping air.
            if (_currentTool == ActiveTool.Sticker && _activeSticker is null)
            {
                try { BtnToolSticker.Flyout.ShowAt(BtnToolSticker); } catch { }
                return;
            }
            pt = ClampToSelection(pt);
            _currentAnnotation = CreateAnnotation(pt);
            _drawStartPoint = pt;
            if (_currentAnnotation != null)
            {
                if (_currentAnnotation is Models.SnipNumber) _nextNumber++;
                _annotations.Add(_currentAnnotation);
                OverlayLog($"ann-add {_currentAnnotation.GetType().Name} total={_annotations.Count}");
                _redoStack.Clear();
                _isDragging = true;
                RootGrid.CapturePointer(e.Pointer);
                DrawCanvas.Invalidate();
            }
            return;
        }

        // With the Select tool on a committed selection, grabbing an annotation
        // moves it instead of the selection box.
        if (_currentTool == ActiveTool.Select && _state == Services.SnipCaptureState.Selected)
        {
            var hit = AnnotationAt(pt);
            if (hit != null)
            {
                ClearAnnotationSelection();
                hit.IsSelected = true;
                _movingAnnotation = hit;
                _moveLast = pt;
                _isDragging = true;
                RootGrid.CapturePointer(e.Pointer);
                DrawCanvas.Invalidate();
                UpdateHud("ann-move-start");
                return;
            }
        }

        SnipToolbar.Visibility = Visibility.Collapsed;
        HideTextEditor();
        _dragMode = HitTest(pt);
        if (_dragMode == DragMode.NewSelection)
        {
            _startPoint = pt;
            _endPoint = pt;
            ClearAnnotationSelection();
        }
        else if (_dragMode == DragMode.RootPan)
        {
            _panAnchor = pt;
            _startPointAnchor = _startPoint;
            _endPointAnchor = _endPoint;
        }
        _isDragging = true;
        if (_dragMode == DragMode.NewSelection) _state = Services.SnipCaptureState.Selecting;
        RootGrid.CapturePointer(e.Pointer);
        DrawCanvas.Invalidate();
        OverlayLog($"press dip=({raw.Position.X:0},{raw.Position.Y:0}) px=({pt.X:0},{pt.Y:0}) mode={_dragMode} tool={_currentTool}");
        UpdateHud("press");
    }

    private void ObserveModifiersLive()
    {
        _mods.ObserveLive(
            KeyDownLive(Windows.System.VirtualKey.Shift),
            KeyDownLive(Windows.System.VirtualKey.Control),
            KeyDownLive(Windows.System.VirtualKey.Menu));
    }

    /// <summary>A lost/cancelled capture must never leave a drag stuck on (that is what makes
    /// "drag to draw a box" stop working after an alt-tab or a focus change).</summary>
    private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndDragWithoutCommit();
    }

    private void RootGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndDragWithoutCommit();
    }

    private void EndDragWithoutCommit()
    {
        if (!_isDragging) return;
        _isDragging = false;
        _currentAnnotation = null;
        _dragMode = DragMode.None;
        DrawCanvas.Invalidate();
    }

    private void BeginTextEditor(Point pt)
    {
        HideTextEditor();
        var pos = ClampToSelection(pt);
        _textEditor = new TextBox
        {
            Width = 260,
            PlaceholderText = "Type text, press Enter",
        };
        _textEditor.KeyDown += (_, ke) =>
        {
            if (ke.Key == Windows.System.VirtualKey.Enter)
            {
                var text = _textEditor.Text;
                var placeAt = pos;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _annotations.Add(new Models.SnipText { Position = placeAt, TextValue = text, Color = _currentColor, FontSize = Math.Max(14f, _currentStrokeThickness * 4) });
                    _redoStack.Clear();
                    DrawCanvas.Invalidate();
                }
                HideTextEditor();
                RootGrid.Focus(FocusState.Programmatic);
                ke.Handled = true;
            }
            else if (ke.Key == Windows.System.VirtualKey.Escape)
            {
                HideTextEditor();
                RootGrid.Focus(FocusState.Programmatic);
                ke.Handled = true;
            }
        };
        // pt is in bitmap pixels; Canvas attached properties use DIPs.
        Canvas.SetLeft(_textEditor, Math.Max(8, Math.Min(pt.X / _dpiScale, _imgWidth / _dpiScale - 270)));
        Canvas.SetTop(_textEditor, Math.Max(8, Math.Min(pt.Y / _dpiScale, _imgHeight / _dpiScale - 44)));
        var host = ToolbarHost?.Parent as Canvas;
        if (host == null) { OverlayLog("texteditor ERR no canvas host"); HideTextEditor(); return; }
        host.Children.Add(_textEditor);
        _textEditor.Focus(FocusState.Programmatic);
    }

    private void HideTextEditor()
    {
        if (_textEditor != null)
        {
            try { (ToolbarHost?.Parent as Canvas)?.Children.Remove(_textEditor); } catch { }
            _textEditor = null;
        }
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var rawDip = e.GetCurrentPoint(RootGrid).Position;
        var pt = ToPixels(rawDip);
        _lastCursorPx = pt;
        ObserveModifiersLive();

        if (_isDragging && _currentTool != ActiveTool.Select && _currentAnnotation != null)
        {
            pt = ClampToSelection(pt);
            // Constraint modifiers apply ONLY to shape annotations, read live, and
            // never while Shift is still held over from the capture hotkey.
            if ((_currentAnnotation is Models.SnipRectangle || _currentAnnotation is Models.SnipEllipse)
                && Services.SnipRegionLogic.ShouldSquareShape(true,
                    KeyDownLive(Windows.System.VirtualKey.Shift), _mods.ShiftPreheld))
            {
                var sq = Services.SnipRegionLogic.ApplySquare(_drawStartPoint.X, _drawStartPoint.Y, pt.X, pt.Y);
                _currentAnnotation.UpdateBounds(new Point(sq.X, sq.Y), new Point(sq.X + sq.W, sq.Y + sq.H));
            }
            else
            {
                _currentAnnotation.UpdateBounds(_drawStartPoint, pt);
            }
            DrawCanvas.Invalidate();
            UpdateHud("move");
            return;
        }

        if (_isDragging && _movingAnnotation != null)
        {
            _movingAnnotation.MoveBy(pt.X - _moveLast.X, pt.Y - _moveLast.Y);
            if (!BoundsWithinSelection(_movingAnnotation.GetBounds()))
                _movingAnnotation.MoveBy(-(pt.X - _moveLast.X), -(pt.Y - _moveLast.Y)); // keep export == canvas
            else
                _moveLast = pt;
            DrawCanvas.Invalidate();
            UpdateHud("move");
            return;
        }

        if (!_isDragging)
        {
            UpdateHoverCursor(rawDip);
            UpdateHud("move");
            return;
        }

        if (_dragMode == DragMode.NewSelection) _endPoint = pt;
        else if (_dragMode == DragMode.TopLeft) _startPoint = pt;
        else if (_dragMode == DragMode.TopCenter) _startPoint = new Point(_startPoint.X, pt.Y);
        else if (_dragMode == DragMode.TopRight) { _startPoint = new Point(_startPoint.X, pt.Y); _endPoint = new Point(pt.X, _endPoint.Y); }
        else if (_dragMode == DragMode.MiddleLeft) _startPoint = new Point(pt.X, _startPoint.Y);
        else if (_dragMode == DragMode.MiddleRight) _endPoint = new Point(pt.X, _endPoint.Y);
        else if (_dragMode == DragMode.BottomLeft) { _startPoint = new Point(pt.X, _startPoint.Y); _endPoint = new Point(_endPoint.X, pt.Y); }
        else if (_dragMode == DragMode.BottomCenter) _endPoint = new Point(_endPoint.X, pt.Y);
        else if (_dragMode == DragMode.BottomRight) _endPoint = pt;
        else if (_dragMode == DragMode.RootPan)
        {
            double dx = pt.X - _panAnchor.X;
            double dy = pt.Y - _panAnchor.Y;
            _startPoint = new Point(_startPointAnchor.X + dx, _startPointAnchor.Y + dy);
            _endPoint = new Point(_endPointAnchor.X + dx, _endPointAnchor.Y + dy);
        }
        DrawCanvas.Invalidate();
    }

    private void UpdateHoverCursor(Point dip)
    {
        try
        {
            double w = Math.Abs(_startPoint.X - _endPoint.X), h = Math.Abs(_startPoint.Y - _endPoint.Y);
            if (_state != Services.SnipCaptureState.Selected || w <= 0 || h <= 0 || _currentTool != ActiveTool.Select)
            {
                SetOverlayCursor(Microsoft.UI.Input.InputSystemCursorShape.Cross);
                return;
            }
            var mode = HitTest(ToPixels(dip));
            var shape = mode switch
            {
                DragMode.TopLeft or DragMode.BottomRight => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
                DragMode.TopRight or DragMode.BottomLeft => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
                DragMode.TopCenter or DragMode.BottomCenter => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
                DragMode.MiddleLeft or DragMode.MiddleRight => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
                DragMode.RootPan => Microsoft.UI.Input.InputSystemCursorShape.SizeAll,
                _ => Microsoft.UI.Input.InputSystemCursorShape.Cross,
            };
            SetOverlayCursor(shape);
        }
        catch { }
    }

    /// <summary>F12 debug HUD + backing data. Shows overlay state, last pointer
    /// event and coordinates, element under the pointer, physical and DIP cursor
    /// position, window rect vs monitor rect, DPI scale, live modifier states and
    /// whether the overlay is the foreground window.</summary>
    private void UpdateHud(string lastEvent)
    {
        try
        {
            var dip = new Point(
                Services.SnipRegionLogic.PhysicalToDips(_lastCursorPx.X, _dpiScale),
                Services.SnipRegionLogic.PhysicalToDips(_lastCursorPx.Y, _dpiScale));
            string under = "?";
            try
            {
                var hits = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(dip, RootGrid);
                var first = hits?.FirstOrDefault();
                under = first?.GetType().Name ?? "(none)";
            }
            catch (Exception ex) { under = "ERR " + ex.GetType().Name; }
            string phys = "?";
            try
            {
                if (GetCursorPos(out WinPoint p)) phys = $"{p.X},{p.Y}";
            }
            catch { }
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            bool fg = false;
            try { fg = GetForegroundWindow() == hwnd; } catch { }
            bool sh = KeyDownLive(Windows.System.VirtualKey.Shift);
            bool ct = KeyDownLive(Windows.System.VirtualKey.Control);
            bool al = KeyDownLive(Windows.System.VirtualKey.Menu);
            double sx = Math.Min(_startPoint.X, _endPoint.X), sy = Math.Min(_startPoint.Y, _endPoint.Y);
            double sw = Math.Abs(_endPoint.X - _startPoint.X), sh2 = Math.Abs(_endPoint.Y - _startPoint.Y);
            HudText.Text =
                $"state={_state} event={lastEvent} tool={_currentTool} drag={_dragMode} dragging={_isDragging}\n" +
                $"cursor dip=({dip.X:0},{dip.Y:0}) px=({_lastCursorPx.X:0},{_lastCursorPx.Y:0}) physical=({phys}) under={under}\n" +
                $"window=({_appWindow.Position.X},{_appWindow.Position.Y} {_appWindow.Size.Width}x{_appWindow.Size.Height}) " +
                $"monitor=({_monX},{_monY} {_monW}x{_monH}) dpi={_dpiScale}\n" +
                $"live(shift,ctrl,alt)=({sh},{ct},{al}) preheld=({_mods.ShiftPreheld},{_mods.CtrlPreheld},{_mods.AltPreheld}) fg={fg}\n" +
                $"sel=({sx:0},{sy:0} {sw:0}x{sh2:0}) ann={_annotations.Count} undo={_redoStack.Count}";
        }
        catch { }
    }

    /// <summary>Anything drawn outside the selection is cropped away on copy/save,
    /// so annotations are clamped into the committed selection: what you draw is
    /// exactly what gets exported.</summary>
    private Point ClampToSelection(Point px)
    {
        double w = Math.Abs(_endPoint.X - _startPoint.X), h = Math.Abs(_endPoint.Y - _startPoint.Y);
        if (w <= 0 || h <= 0) return px;
        double x0 = Math.Min(_startPoint.X, _endPoint.X), y0 = Math.Min(_startPoint.Y, _endPoint.Y);
        return new Point(Math.Max(x0, Math.Min(px.X, x0 + w)), Math.Max(y0, Math.Min(px.Y, y0 + h)));
    }

    private bool BoundsWithinSelection(Windows.Foundation.Rect b)
    {
        double w = Math.Abs(_endPoint.X - _startPoint.X), h = Math.Abs(_endPoint.Y - _startPoint.Y);
        if (w <= 0 || h <= 0) return true;
        double x0 = Math.Min(_startPoint.X, _endPoint.X), y0 = Math.Min(_startPoint.Y, _endPoint.Y);
        return b.X >= x0 - 1 && b.Y >= y0 - 1 && b.X + b.Width <= x0 + w + 1 && b.Y + b.Height <= y0 + h + 1;
    }

    private void PositionToolbar()
    {
        float x = (float)Math.Min(_startPoint.X, _endPoint.X);
        float y = (float)Math.Min(_startPoint.Y, _endPoint.Y);
        float w = (float)Math.Abs(_startPoint.X - _endPoint.X);
        float h = (float)Math.Abs(_startPoint.Y - _endPoint.Y);
        if (w <= 0 || h <= 0) { ToolbarHost.Visibility = Visibility.Collapsed; return; }
        if (_dpiScale <= 0) _dpiScale = 1.0;

        ToolbarHost.Visibility = Visibility.Visible;
        SnipToolbar.Visibility = Visibility.Visible;
        ToolbarHost.UpdateLayout();
        double tbW = ToolbarHost.ActualWidth > 0 ? ToolbarHost.ActualWidth : 560;
        double tbH = ToolbarHost.ActualHeight > 0 ? ToolbarHost.ActualHeight : 80;

        double left = x + w - tbW;
        double top;
        if (y + h + 10 + tbH <= _imgHeight)
            top = y + h + 10;
        else if (y - 10 - tbH >= 0)
            top = y - 10 - tbH;
        else
            top = _imgHeight - tbH - 8;

        // Coordinates are in bitmap pixels; Canvas attached properties use DIPs.
        double pxW = tbW * _dpiScale, pxH = tbH * _dpiScale;
        left = Math.Max(8, Math.Min(left, _imgWidth - pxW - 8));
        top = Math.Max(8, Math.Min(top, _imgHeight - pxH - 8));

        Canvas.SetLeft(ToolbarHost, left / _dpiScale);
        Canvas.SetTop(ToolbarHost, top / _dpiScale);
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging && _currentTool != ActiveTool.Select && _currentAnnotation != null)
        {
            _isDragging = false;
            _currentAnnotation = null;
            RootGrid.ReleasePointerCapture(e.Pointer);
            DrawCanvas.Invalidate();
            return;
        }

        if (_isDragging)
        {
            _isDragging = false;
            if (_movingAnnotation != null)
            {
                _movingAnnotation = null;
                try { RootGrid.ReleasePointerCapture(e.Pointer); } catch { }
                DrawCanvas.Invalidate();
                PositionToolbar();
                UpdateHud("ann-move-end");
                return;
            }
            if (_dragMode != DragMode.RootPan) _endPoint = ToPixels(e.GetCurrentPoint(RootGrid).Position);

            double minX = Math.Min(_startPoint.X, _endPoint.X);
            double maxX = Math.Max(_startPoint.X, _endPoint.X);
            double minY = Math.Min(_startPoint.Y, _endPoint.Y);
            double maxY = Math.Max(_startPoint.Y, _endPoint.Y);
            _startPoint = new Point(minX, minY);
            _endPoint = new Point(maxX, maxY);

            RootGrid.ReleasePointerCapture(e.Pointer);

            // A click with no drag selects the hovered window; it never creates a
            // default-sized box. A missed click collapses any selection.
            if (_currentTool == ActiveTool.Select && _dragMode == DragMode.NewSelection
                && Services.SnipRegionLogic.IsClick(_pressPx.X, _pressPx.Y, _endPoint.X, _endPoint.Y))
            {
                if (!SnapClickToWindow())
                {
                    _startPoint = _pressPx;
                    _endPoint = _pressPx;
                    _state = Services.SnipCaptureState.Idle;
                }
            }
            else if (Math.Abs(_endPoint.X - _startPoint.X) > 0 && Math.Abs(_endPoint.Y - _startPoint.Y) > 0)
            {
                _state = Services.SnipCaptureState.Selected;
                int rx = _monX + (int)Math.Min(_startPoint.X, _endPoint.X);
                int ry = _monY + (int)Math.Min(_startPoint.Y, _endPoint.Y);
                Services.SnipService.LastRegion = (rx, ry,
                    (int)Math.Abs(_endPoint.X - _startPoint.X),
                    (int)Math.Abs(_endPoint.Y - _startPoint.Y));
            }

            DrawCanvas.Invalidate();
            PositionToolbar();
            OverlayLog($"release px=({_endPoint.X:0},{_endPoint.Y:0}) state={_state} mode={_dragMode}");
            UpdateHud("release");

            if (_state == Services.SnipCaptureState.Selected)
            {
                try { ((App)Microsoft.UI.Xaml.Application.Current).Sniper.DismissOtherMonitors(this); } catch { }
            }

            if (_state == Services.SnipCaptureState.Selected && BtnInstantMode.IsChecked == true && Math.Abs(_endPoint.X - _startPoint.X) > 10 && Math.Abs(_endPoint.Y - _startPoint.Y) > 10)
            {
                // Instant mode is fire-and-forget: copy (which also files it in the
                // gallery) and get off the screen. Turn it off for the editor toolbar.
                OverlayLog("instant-copy firing");
                _ = CopySelectionToClipboardAsync().ContinueWith(t =>
                {
                    try { DispatcherQueue.TryEnqueue(() => this.Close()); } catch { }
                });
            }
        }
    }

    /// <summary>Selects the topmost DWM window under the press point (click with no
    /// drag). Returns false when no window was hit: the caller collapses instead.</summary>
    private bool SnapClickToWindow()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int sx = (int)Math.Round(_pressPx.X) + _monX, sy = (int)Math.Round(_pressPx.Y) + _monY;
            foreach (var r in Services.SnipWindowSnap.GetWindowRects(hwnd))
            {
                if (!r.Contains(sx, sy)) continue;
                var sel = Services.SnipRegionLogic.ClampToImage(
                    Services.SnipRegionLogic.Normalize(r.Left - _monX, r.Top - _monY, r.Right - _monX, r.Bottom - _monY),
                    _imgWidth, _imgHeight);
                if (sel.W < 2 || sel.H < 2) continue;
                _startPoint = new Point(sel.X, sel.Y);
                _endPoint = new Point(sel.X + sel.W, sel.Y + sel.H);
                _state = Services.SnipCaptureState.Selected;
                OverlayLog($"click-snap window=({r.Left},{r.Top} {r.Width}x{r.Height})");
                return true;
            }
        }
        catch (Exception ex) { OverlayLog("snap ERR " + ex.Message); }
        return false;
    }

    private byte[] GetCroppedPixels(out int cW, out int cH)
    {
        int x = (int)Math.Max(0, Math.Min(_startPoint.X, _endPoint.X));
        int y = (int)Math.Max(0, Math.Min(_startPoint.Y, _endPoint.Y));
        cW = (int)Math.Min(_imgWidth - x, Math.Abs(_startPoint.X - _endPoint.X));
        cH = (int)Math.Min(_imgHeight - y, Math.Abs(_startPoint.Y - _endPoint.Y));
        if (cW <= 0 || cH <= 0) return Array.Empty<byte>();

        var win2device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        using var rtb = new Microsoft.Graphics.Canvas.CanvasRenderTarget(win2device, cW, cH, 96);
        using (var ds = rtb.CreateDrawingSession())
        {
            ds.Clear(Microsoft.UI.Colors.Transparent);
            using var bmp = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(win2device, _bgraPixels!, _imgWidth, _imgHeight, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            ds.DrawImage(bmp, new Rect(0, 0, cW, cH), new Rect(x, y, cW, cH));

            var oldTransform = ds.Transform;
            ds.Transform = System.Numerics.Matrix3x2.CreateTranslation(-x, -y);
            foreach (var ann in _annotations)
            {
                if (ann is Models.SnipSpotlight sp) { sp.ScreenWidth = _imgWidth; sp.ScreenHeight = _imgHeight; }
                // Background passed so blur/pixelate redactions flatten into the
                // exported pixels instead of being silently dropped.
                ann.Draw(ds, bmp);
            }
            ds.Transform = oldTransform;
        }

        return rtb.GetPixelBytes();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[] cropped = GetCroppedPixels(out int w, out int h);
            if (w == 0 || h == 0) return;

            var pinWin = new Views.SnipPinWindow(cropped, w, h);
            pinWin.Activate();
            this.Close();
        }
        catch (Exception ex)
        {
            OverlayLog("pin ERR " + ex.GetType().Name + ": " + ex.Message);
            ShowToast("Pin failed", ex.Message);
        }
    }

    private async void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        // The overlay always goes away after a copy attempt (success or Fail gets a
        // toast either way) so a finished snip can never stick on screen.
        await CopySelectionToClipboardAsync();
        this.Close();
    }

    private async Task<bool> CopySelectionToClipboardAsync()
    {
        try
        {
            byte[] cropped = GetCroppedPixels(out int w, out int h);
            OverlayLog($"copy sel={w}x{h} ann={_annotations.Count}");
            if (w == 0 || h == 0) return false;

            var win2device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            var bmp = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(win2device, cropped, w, h, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);

            var mStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await bmp.SaveAsync(mStream, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);

            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(mStream));
            try
            {
                // CF_DIB alongside the PNG: pass the stream itself (both a stream
                // reference and raw bytes throw "data type mismatch" here).
                // Best-effort only - the PNG covers all modern paste targets.
                byte[] dib = Services.SnipRegionLogic.BuildDib32(cropped, w, h);
                var dibStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await dibStream.WriteAsync(dib.AsBuffer());
                dibStream.Seek(0);
                dp.SetData("DeviceIndependentBitmap", dibStream);
            }
            catch (Exception ex) { OverlayLog("DIB WARN " + ex.Message); }
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);

            // Every copy is also kept in the gallery (same annotated pixels).
            // No success toast: captures are silent, failures still shout.
            try
            {
                var saved = await SnipGalleryService.SaveSnipAsync(cropped, w, h, SourceApp);
                OverlayLog($"copy gallery-saved {saved.Path}");
                Services.SnipService.NotifyGalleryChanged();
            }
            catch (Exception ex) { OverlayLog("copy gallery-save WARN " + ex.Message); }

            OverlayLog($"copy done bytes={cropped.Length}");
            return true;
        }
        catch (Exception ex)
        {
            OverlayLog("copy ERR " + ex.GetType().Name + ": " + ex.Message);
            ShowToast("Copy failed", ex.Message);
            return false;
        }
    }

    /// <summary>Fills exactly the captured monitor rectangle (physical pixels), so the frozen
    /// frame maps 1:1 to the window client area and every pointer coordinate is a screen pixel.</summary>
    public void PlaceOnMonitor(int x, int y, int width, int height)
    {
        _monX = x; _monY = y; _monW = width; _monH = height;
        try
        {
            if (width > 0 && height > 0)
                _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
            else
                _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        catch { }
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        byte[] cropped = GetCroppedPixels(out int w, out int h);
        if (w == 0 || h == 0) return;

        try
        {
            var result = await SnipGalleryService.SaveSnipAsync(cropped, w, h, SourceApp);
            OverlayLog($"save ok {result.Path} uniform={result.IsUniform}");
            Services.SnipService.NotifyGalleryChanged();
            if (result.IsUniform)
                ShowToast("Snip saved",
                    "Warning: the capture is a single flat color - the app may have blocked capture (DRM/protected content).");
        }
        catch (Exception ex)
        {
            ShowToast("Save failed", ex.Message);
            return;
        }
        this.Close();
    }

    private void ShowToast(string title, string content)
    {
        // Notifications are off by user request: log only, never pop, never clickable.
        OverlayLog($"toast-suppressed [{title}] {content}");
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

    private void BtnTool_Click(object sender, RoutedEventArgs e)
    {
        foreach (var btn in ToolButtons) btn.IsChecked = false;
        if (sender is ToggleButton clicked)
        {
            clicked.IsChecked = true;
            _currentTool = (ActiveTool)Enum.Parse(typeof(ActiveTool), (string)clicked.Tag);

            if (!string.IsNullOrEmpty(clicked.Name))
            {
                var settings = SnipSettingsService.Load();
                settings.ActiveTool = (string)clicked.Tag;
                SnipSettingsService.Save(settings);
            }
        }
        UpdateFormatRow();
    }

    /// <summary>Shows the contextual format row only for tools that use it.</summary>
    private void UpdateFormatRow()
    {
        if (FormatRow is null || BtnFillToggle is null || FillSeparator is null || SliderThickness is null) return;
        bool show = _currentTool is ActiveTool.Arrow or ActiveTool.Line or ActiveTool.Rectangle
            or ActiveTool.Ellipse or ActiveTool.Ink or ActiveTool.Highlight
            or ActiveTool.Text or ActiveTool.Number or ActiveTool.Spotlight;
        FormatRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        bool fillApplies = _currentTool is ActiveTool.Rectangle or ActiveTool.Ellipse;
        BtnFillToggle.Visibility = fillApplies ? Visibility.Visible : Visibility.Collapsed;
        FillSeparator.Visibility = fillApplies ? Visibility.Visible : Visibility.Collapsed;

        // For the text tool the "thickness" slider acts as font size.
        SliderThickness.Minimum = 1;
        SliderThickness.Maximum = 20;
        UpdatePreviewDot();
    }

    private void UpdatePreviewDot()
    {
        if (ThicknessPreview is null) return;
        double d = Math.Max(4, Math.Min(20, _currentStrokeThickness));
        ThicknessPreview.Width = d;
        ThicknessPreview.Height = d;
    }

    private void UpdateInstantModeVisual()
    {
        if (BtnInstantMode is null || InstantCheckBadge is null) return;
        bool on = BtnInstantMode.IsChecked == true;
        InstantCheckBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(BtnInstantMode, on ? "Instant copy: ON" : "Instant copy: OFF");
    }

    private void BtnInstantMode_Click(object sender, RoutedEventArgs e)
    {
        UpdateInstantModeVisual();
        var settings = SnipSettingsService.Load();
        settings.InstantMode = BtnInstantMode.IsChecked == true;
        SnipSettingsService.Save(settings);
    }

    private Models.SnipObject? ResizeTarget() =>
        _annotations.FirstOrDefault(a => a.IsSelected);

    private static bool IsResizableKind(Models.SnipObject a) => a is Models.SnipRectangle
        or Models.SnipEllipse or Models.SnipRedactionBox or Models.SnipSpotlight
        or Models.SnipSticker or Models.SnipArrow or Models.SnipLine;

    private void ResizeFlyout_Opening(object? sender, object e)
    {
        var target = ResizeTarget();
        bool ok = target != null && IsResizableKind(target);
        ResizeHint.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        ResizeWidthBox.IsEnabled = ok;
        ResizeHeightBox.IsEnabled = ok;
        ResizeLockBox.IsEnabled = ok;
        ResizeApplyButton.IsEnabled = ok;
        if (ok)
        {
            try
            {
                var b = target!.GetBounds();
                ResizeWidthBox.Value = Math.Max(8, Math.Min(8192, Math.Round(b.Width)));
                ResizeHeightBox.Value = Math.Max(8, Math.Min(8192, Math.Round(b.Height)));
            }
            catch { }
        }
    }

    private void BtnResizeApply_Click(object sender, RoutedEventArgs e)
    {
        var target = ResizeTarget();
        if (target == null || !IsResizableKind(target)) return;
        try
        {
            double reqW = Math.Max(8, Math.Min(8192, ResizeWidthBox.Value));
            double reqH = Math.Max(8, Math.Min(8192, ResizeHeightBox.Value));
            var b = target.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return;
            if (ResizeLockBox.IsChecked == true)
            {
                double s = Math.Min(reqW / b.Width, reqH / b.Height);
                reqW = Math.Max(8, b.Width * s);
                reqH = Math.Max(8, b.Height * s);
            }
            double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            switch (target)
            {
                case Models.SnipRectangle r:
                    r.Bounds = CenteredRect(cx, cy, reqW, reqH); break;
                case Models.SnipEllipse el:
                    el.Bounds = CenteredRect(cx, cy, reqW, reqH); break;
                case Models.SnipRedactionBox bx:
                    bx.Bounds = CenteredRect(cx, cy, reqW, reqH); break;
                case Models.SnipSpotlight sp:
                    sp.Bounds = CenteredRect(cx, cy, reqW, reqH); break;
                case Models.SnipSticker st:
                    st.Bounds = CenteredRect(cx, cy, reqW, reqH); break;
                case Models.SnipArrow ar:
                    ScalePoints(ar, cx, cy, reqW / b.Width, reqH / b.Height); break;
                case Models.SnipLine ln:
                    ScalePoints(ln, cx, cy, reqW / b.Width, reqH / b.Height); break;
                default: return;
            }
            DrawCanvas.Invalidate();
            UpdateHud("ann-resize");
            OverlayLog($"ann-resize {target.GetType().Name} -> {reqW:0}x{reqH:0}");
        }
        catch (Exception ex) { OverlayLog("ann-resize ERR " + ex.Message); }
    }

    private static Rect CenteredRect(double cx, double cy, double w, double h) =>
        new Rect(cx - w / 2, cy - h / 2, w, h);

    private static void ScalePoints(Models.SnipObject line, double cx, double cy, double sx, double sy)
    {
        if (line is Models.SnipArrow ar)
        {
            ar.Start = new Point(cx + (ar.Start.X - cx) * sx, cy + (ar.Start.Y - cy) * sy);
            ar.End = new Point(cx + (ar.End.X - cx) * sx, cy + (ar.End.Y - cy) * sy);
        }
        else if (line is Models.SnipLine ln)
        {
            ln.Start = new Point(cx + (ln.Start.X - cx) * sx, cy + (ln.Start.Y - cy) * sy);
            ln.End = new Point(cx + (ln.End.X - cx) * sx, cy + (ln.End.Y - cy) * sy);
        }
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_annotations.Count > 0)
        {
            var last = _annotations[_annotations.Count - 1];
            _annotations.RemoveAt(_annotations.Count - 1);
            if (last is Models.SnipNumber) _nextNumber = Math.Max(1, _nextNumber - 1);
            _redoStack.Add(last);
            DrawCanvas.Invalidate();
        }
    }

    private void BtnRedo_Click(object sender, RoutedEventArgs e)
    {
        if (_redoStack.Count > 0)
        {
            var last = _redoStack[_redoStack.Count - 1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            if (last is Models.SnipNumber) _nextNumber++;
            _annotations.Add(last);
            DrawCanvas.Invalidate();
        }
    }

    private async void BtnOcr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[] cropped = GetCroppedPixels(out int w, out int h);
            if (w == 0 || h == 0) return;

            var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            using var bmp = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(device, cropped, w, h, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Premultiplied);

            using var swBitmap = await Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromSurfaceAsync(bmp);
            var swBitmapTarget = swBitmap.BitmapPixelFormat != Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8
                 ? Windows.Graphics.Imaging.SoftwareBitmap.Convert(swBitmap, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8)
                 : swBitmap;

            var ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine != null)
            {
                var ocrResult = await ocrEngine.RecognizeAsync(swBitmapTarget);
                if (!string.IsNullOrWhiteSpace(ocrResult.Text))
                {
                    var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dataPackage.SetText(ocrResult.Text);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                    ShowToast("Text extracted and copied", "The text from the snip was saved to your clipboard.");
                }
                else
                {
                    ShowToast("No text detected", "OCR could not recognize any characters.");
                }
            }
            else
            {
                ShowToast("OCR Unavailable", "No language model found. Please install a language pack.");
            }
        }
        catch (Exception ex)
        {
            ShowToast("OCR Failed", ex.Message);
        }
    }

    private async void BtnAutoRedact_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[] cropped = GetCroppedPixels(out int w, out int h);
            if (w == 0 || h == 0) return;

            var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            using var bmp = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(device, cropped, w, h, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Premultiplied);

            using var swBitmap = await Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromSurfaceAsync(bmp);
            var swBitmapTarget = swBitmap.BitmapPixelFormat != Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8
                 ? Windows.Graphics.Imaging.SoftwareBitmap.Convert(swBitmap, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8)
                 : swBitmap;

            var ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine == null)
            {
                ShowToast("Auto-Redact Unavailable", "No language model found. Please install a language pack.");
                return;
            }

            var bounds = await kaliteConfig.Services.SnipToolManager.CalculateRedactionBoundsAsync(swBitmapTarget);
            if (bounds.Count == 0)
            {
                ShowToast("Auto-Redact", "No sensitive PII patterns were detected in the image.");
                return;
            }

            // Remap boundaries to global SnipOverlayWindow positions
            double offsetX = Math.Max(0, Math.Min(_startPoint.X, _endPoint.X));
            double offsetY = Math.Max(0, Math.Min(_startPoint.Y, _endPoint.Y));

            foreach (var r in bounds)
            {
                var redacted = new Models.SnipRedactionBox 
                { 
                    Bounds = new Rect(offsetX + r.X, offsetY + r.Y, r.Width, r.Height),
                    PixelateMode = false 
                };
                _annotations.Add(redacted);
            }

            _redoStack.Clear();
            DrawCanvas.Invalidate();
            ShowToast("Auto-Redact Applied", $"{bounds.Count} instances of sensitive data blurred.");
        }
        catch (Exception ex)
        {
            ShowToast("Auto-Redact Error", ex.Message);
        }
    }

    private void SliderThickness_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _currentStrokeThickness = (float)e.NewValue;
        UpdatePreviewDot();
        var settings = SnipSettingsService.Load();
        settings.Thickness = (float)e.NewValue;
        SnipSettingsService.Save(settings);
    }

    private void ColorPickerTool_ColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs args)
    {
        _currentColor = args.NewColor;
        var settings = SnipSettingsService.Load();
        settings.ColorHex = args.NewColor.ToString();
        SnipSettingsService.Save(settings);
    }

    private void BtnFillToggle_Changed(object sender, RoutedEventArgs e) { }
}
