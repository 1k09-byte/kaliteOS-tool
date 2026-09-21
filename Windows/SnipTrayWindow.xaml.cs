using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Media.Imaging;
using kaliteConfig.Services;

namespace kaliteConfig.Views;

public sealed partial class SnipTrayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly SnipTrayLogic _logic;
    private DispatcherTimer _timer;
    private SoftwareBitmap _img;
    private Windows.Storage.StorageFile? _file;

    public SnipTrayWindow(SoftwareBitmap img, Windows.Storage.StorageFile? file = null)
    {
        this.InitializeComponent();
        _img = img;
        _file = file;

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var wId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(wId);

        ExtendsContentIntoTitleBar = true;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // Apply raw Win32 for NoActivate & ToolWindow so it doesn't steal focus or show in taskbar
        int exstyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        SetWindowLong(hWnd, GWL_EXSTYLE, exstyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
        
        var display = DisplayArea.Primary;
        var r = display.WorkArea;
        
        int trayW = 260;
        int trayH = 180;
        int padding = 20;
        int startX = r.Width - trayW - padding;
        int startY = r.Height - trayH - padding;
        
        SetWindowPos(hWnd, HWND_TOPMOST, startX, startY, trayW, trayH, SWP_NOACTIVATE);
        
        _logic = new SnipTrayLogic();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _timer.Tick += (s, e) => { this.Close(); };
        _timer.Start();

        _holdTracker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _holdTracker.Tick += (s, e) => 
        {
            if (_logic.CheckHold(DateTime.Now))
            {
                _holdTracker.Stop();
                var st = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = 0.92, ScaleY = 0.92, CenterX = this.Bounds.Width / 2, CenterY = this.Bounds.Height / 2 };
                TrayBorder.RenderTransform = st;
            }
        };

        LoadImageAsync(img);
    }
    
    private DispatcherTimer _holdTracker;

    private async void LoadImageAsync(SoftwareBitmap bitmap)
    {
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        PreviewImage.Source = source;
    }

    private void OnDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        var dp = args.Data;
        if (_file != null)
        {
            dp.SetStorageItems(new[] { _file });
        }
        
        try
        {
            // Fallback for Discord/Paint
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoderTask = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask();
            encoderTask.Wait();
            var encoder = encoderTask.Result;
            encoder.SetSoftwareBitmap(_img);
            var flushTask = encoder.FlushAsync().AsTask();
            flushTask.Wait();
            stream.Seek(0);
            dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to bind bitmap to OLE drag: {ex.Message}");
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _timer.Stop();
        var pt = e.GetCurrentPoint(this.Content).Position;
        _logic.PointerPressed((float)pt.X, (float)pt.Y, DateTime.Now);
        ((UIElement)sender).CapturePointer(e.Pointer);
        
        TrayBorder.RenderTransform = null;
        _holdTracker.Start();
    }

    private async void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this.Content).Position;
        var state = _logic.PointerMoved((float)pt.X, (float)pt.Y, DateTime.Now);
        
        if (state == SnipTrayLogic.GestureState.Dragging)
        {
            // Initiate OLE Drag
            try
            {
                var uiElement = (UIElement)sender;
                uiElement.ReleasePointerCapture(e.Pointer); // Must release capture before standard WinUI OLE Drag

                var result = await uiElement.StartDragAsync(e.GetCurrentPoint(uiElement));
                
                if (result == Windows.ApplicationModel.DataTransfer.DataPackageOperation.None)
                {
                    ShowErrorOverlay("Drag failed: Elevation mismatch");
                }
            }
            catch (Exception ex)
            {
                ShowErrorOverlay("Drag failed");
            }
        }
    }

    private void ShowErrorOverlay(string msg)
    {
        ErrorText.Text = msg;
        ErrorOverlay.Opacity = 1;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        t.Tick += (s, e) => { ErrorOverlay.Opacity = 0; t.Stop(); };
        t.Start();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _holdTracker.Stop();
        TrayBorder.RenderTransform = null;

        var pt = e.GetCurrentPoint(this.Content).Position;
        var finalState = _logic.PointerReleased((float)pt.X, (float)pt.Y, DateTime.Now);

        if (finalState == SnipTrayLogic.GestureState.Swiping)
        {
            this.Close(); // Swipe to dismiss
        }
        else if (finalState == SnipTrayLogic.GestureState.None)
        {
            // Tap! Open Editor
            var app = (App)Application.Current;
            if (_file != null)
            {
                app.Sniper.OpenInEditor(_file.Path);
            }
            else
            {
                _ = app.Sniper.OpenInEditorAsync(_img);
            }
            this.Close();
        }
        else
        {
            _timer.Start(); // Resume timer
        }
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        if (_file != null)
        {
            app.Sniper.OpenInEditor(_file.Path);
        }
        else
        {
            _ = app.Sniper.OpenInEditorAsync(_img);
        }
        this.Close();
    }

    private async void MenuCopy_Click(object sender, RoutedEventArgs e)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        
        var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(_img);
        await encoder.FlushAsync();
        stream.Seek(0);
        
        dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
        if (_file != null)
        {
            dp.SetStorageItems(new[] { _file });
        }
        
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        this.Close();
    }

    private async void MenuSave_Click(object sender, RoutedEventArgs e)
    {
        var savePicker = new Windows.Storage.Pickers.FileSavePicker();
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);
        savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        savePicker.FileTypeChoices.Add("PNG Image", new[] { ".png" });
        savePicker.SuggestedFileName = "Snipping";
        
        var file = await savePicker.PickSaveFileAsync();
        if (file != null)
        {
            using var rstream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, rstream);
            encoder.SetSoftwareBitmap(_img);
            await encoder.FlushAsync();
            this.Close();
        }
    }

    private void MenuDismiss_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
