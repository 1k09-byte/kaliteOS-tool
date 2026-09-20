using System;
using System.Collections.Generic;
using System.Linq;
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

    private bool _isDragging;
    private Point _startPoint;
    private Point _endPoint;

    private enum ActiveTool { Select, Arrow, Line, Rectangle, Ellipse, Ink, Highlight, Text, Number, Blur, Spotlight }
    private ActiveTool _currentTool = ActiveTool.Select;
    private int _nextNumber = 1;

    private List<Models.SnipObject> _annotations = new();
    private List<Models.SnipObject> _redoStack = new();
    private Models.SnipObject? _currentAnnotation;
    private Point _drawStartPoint;

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

    public SnipOverlayWindow()
    {
        this.InitializeComponent();

        _appWindow = this.AppWindow;
        _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        this.ExtendsContentIntoTitleBar = true;

        BuildSwatches();
        UpdateInstantModeVisual();
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Focus(FocusState.Programmatic);
        ApplySavedSettings();
        UpdateFormatRow();
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
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            this.Close();
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
                    ann.Draw(args.DrawingSession);
                }

                args.DrawingSession.DrawRectangle(x, y, w, h, Microsoft.UI.Colors.White, 2);

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
        if (w <= 0 || h <= 0) return DragMode.NewSelection;

        bool IsHit(float hX, float hY) => Math.Abs(pt.X - hX) <= 12 && Math.Abs(pt.Y - hY) <= 12;

        if (IsHit(x, y)) return DragMode.TopLeft;
        if (IsHit(x + w / 2, y)) return DragMode.TopCenter;
        if (IsHit(x + w, y)) return DragMode.TopRight;
        if (IsHit(x, y + h / 2)) return DragMode.MiddleLeft;
        if (IsHit(x + w, y + h / 2)) return DragMode.MiddleRight;
        if (IsHit(x, y + h)) return DragMode.BottomLeft;
        if (IsHit(x + w / 2, y + h)) return DragMode.BottomCenter;
        if (IsHit(x + w, y + h)) return DragMode.BottomRight;

        if (pt.X >= x && pt.X <= x + w && pt.Y >= y && pt.Y <= y + h) return DragMode.RootPan;
        return DragMode.NewSelection;
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
            default: return null;
        }
    }

    private void DrawCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(DrawCanvas).Position;

        // Text annotation: click places an inline editor; the object is
        // committed when the user presses Enter.
        if (_currentTool == ActiveTool.Text && SnipToolbar.Visibility == Visibility.Visible)
        {
            BeginTextEditor(pt);
            return;
        }

        if (_currentTool != ActiveTool.Select && SnipToolbar.Visibility == Visibility.Visible)
        {
            _currentAnnotation = CreateAnnotation(pt);
            _drawStartPoint = pt;
            if (_currentAnnotation != null)
            {
                if (_currentAnnotation is Models.SnipNumber) _nextNumber++;
                _annotations.Add(_currentAnnotation);
                _redoStack.Clear();
                _isDragging = true;
                DrawCanvas.CapturePointer(e.Pointer);
                DrawCanvas.Invalidate();
            }
            return;
        }

        SnipToolbar.Visibility = Visibility.Collapsed;
        HideTextEditor();
        _dragMode = HitTest(pt);
        if (_dragMode == DragMode.NewSelection)
        {
            _startPoint = pt;
            _endPoint = pt;
        }
        else if (_dragMode == DragMode.RootPan)
        {
            _panAnchor = pt;
            _startPointAnchor = _startPoint;
            _endPointAnchor = _endPoint;
        }
        _isDragging = true;
        DrawCanvas.CapturePointer(e.Pointer);
        DrawCanvas.Invalidate();
    }

    private void BeginTextEditor(Point pt)
    {
        HideTextEditor();
        var pos = pt;
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
        Canvas.SetLeft(_textEditor, Math.Max(8, Math.Min(pt.X, _imgWidth - 270)));
        Canvas.SetTop(_textEditor, Math.Max(8, Math.Min(pt.Y, _imgHeight - 44)));
        ((Canvas)SnipToolbar.Parent).Children.Add(_textEditor);
        _textEditor.Focus(FocusState.Programmatic);
    }

    private void HideTextEditor()
    {
        if (_textEditor != null)
        {
            ((Canvas)SnipToolbar.Parent).Children.Remove(_textEditor);
            _textEditor = null;
        }
    }

    private void DrawCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(DrawCanvas).Position;

        if (_isDragging && _currentTool != ActiveTool.Select && _currentAnnotation != null)
        {
            _currentAnnotation.UpdateBounds(_drawStartPoint, pt);
            DrawCanvas.Invalidate();
            return;
        }

        if (!_isDragging) return;

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

    private void PositionToolbar()
    {
        float x = (float)Math.Min(_startPoint.X, _endPoint.X);
        float y = (float)Math.Min(_startPoint.Y, _endPoint.Y);
        float w = (float)Math.Abs(_startPoint.X - _endPoint.X);
        float h = (float)Math.Abs(_startPoint.Y - _endPoint.Y);
        if (w <= 0 || h <= 0) { ToolbarHost.Visibility = Visibility.Collapsed; return; }

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

        left = Math.Max(8, Math.Min(left, _imgWidth - tbW - 8));
        top = Math.Max(8, Math.Min(top, _imgHeight - tbH - 8));

        Canvas.SetLeft(ToolbarHost, left);
        Canvas.SetTop(ToolbarHost, top);
    }

    private void DrawCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging && _currentTool != ActiveTool.Select && _currentAnnotation != null)
        {
            _isDragging = false;
            _currentAnnotation = null;
            DrawCanvas.ReleasePointerCapture(e.Pointer);
            DrawCanvas.Invalidate();
            return;
        }

        if (_isDragging)
        {
            _isDragging = false;
            if (_dragMode != DragMode.RootPan) _endPoint = e.GetCurrentPoint(DrawCanvas).Position;

            double minX = Math.Min(_startPoint.X, _endPoint.X);
            double maxX = Math.Max(_startPoint.X, _endPoint.X);
            double minY = Math.Min(_startPoint.Y, _endPoint.Y);
            double maxY = Math.Max(_startPoint.Y, _endPoint.Y);
            _startPoint = new Point(minX, minY);
            _endPoint = new Point(maxX, maxY);

            DrawCanvas.ReleasePointerCapture(e.Pointer);
            DrawCanvas.Invalidate();
            PositionToolbar();

            if (BtnInstantMode.IsChecked == true && Math.Abs(_endPoint.X - _startPoint.X) > 10 && Math.Abs(_endPoint.Y - _startPoint.Y) > 10)
            {
                _ = CopySelectionToClipboardAsync();
            }
        }
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
                ann.Draw(ds);
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
        byte[] cropped = GetCroppedPixels(out int w, out int h);
        if (w == 0 || h == 0) return;

        var pinWin = new Views.SnipPinWindow(cropped, w, h);
        pinWin.Activate();
        this.Close();
    }

    private async void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (await CopySelectionToClipboardAsync())
            this.Close();
    }

    private async Task<bool> CopySelectionToClipboardAsync()
    {
        byte[] cropped = GetCroppedPixels(out int w, out int h);
        if (w == 0 || h == 0) return false;

        var win2device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        var bmp = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(win2device, cropped, w, h, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);

        var mStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await bmp.SaveAsync(mStream, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);

        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(mStream));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);

        ShowToast("Copied to clipboard", "The snip was saved to your clipboard.");
        return true;
    }

    public void PlaceOnMonitor(int x, int y)
    {
        try { _appWindow.Move(new Windows.Graphics.PointInt32(x, y)); } catch { }
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        byte[] cropped = GetCroppedPixels(out int w, out int h);
        if (w == 0 || h == 0) return;

        try
        {
            var result = await SnipGalleryService.SaveSnipAsync(cropped, w, h);
            ShowToast("Snip saved",
                (result.IsUniform
                    ? "Warning: the capture is a single flat color — the app may have blocked capture (DRM/protected content)."
                    : $"Saved to {System.IO.Path.GetFileName(result.Path)} in Pictures\\kaliteConfig Snips.")
                + " It appears in the Gallery immediately.");
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
        try
        {
            var xml = $@"
            <toast>
                <visual>
                    <binding template='ToastGeneric'>
                        <text>{EscapeXml(title)}</text>
                        <text>{EscapeXml(content)}</text>
                    </binding>
                </visual>
            </toast>";
            var doc = new Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(xml);
            var toast = new Windows.UI.Notifications.ToastNotification(doc);
            Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier().Show(toast);
        } catch { }
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
        double d = Math.Max(4, Math.Min(20, _currentStrokeThickness));
        ThicknessPreview.Width = d;
        ThicknessPreview.Height = d;
    }

    private void UpdateInstantModeVisual()
    {
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
        }
        catch (Exception ex)
        {
            ShowToast("OCR Failed", ex.Message);
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
