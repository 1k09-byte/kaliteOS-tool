using System;
using System.Threading.Tasks;

// Minimal stand-ins so the REAL SnipGalleryService.cs / SnipPreviewService.cs compile in this
// headless harness. BitmapImage is a XAML object; the IO tests never create one for real.
namespace Microsoft.UI.Xaml
{
    /// <summary>Stand-in for the XAML failure args: only the message survives.</summary>
    public sealed class ExceptionRoutedEventArgs
    {
        public string ErrorMessage { get; set; } = "";
    }
}

namespace Microsoft.UI.Xaml.Media.Imaging
{
    public sealed class BitmapImage
    {
        public event EventHandler<object>? ImageOpened;

        public event EventHandler<Microsoft.UI.Xaml.ExceptionRoutedEventArgs>? ImageFailed;

        public int DecodePixelWidth { get; set; }

        /// <summary>Reported as 1x1 so the "wait until the pixels are resident" path in
        /// SnipGalleryService skips its decode wait here: a headless harness has no real decoder,
        /// and the pipeline under test is the disk tier cache, not XAML decode.</summary>
        public int PixelWidth { get; private set; } = 1;

        public int PixelHeight { get; private set; } = 1;

        public Task SetSourceAsync(Windows.Storage.Streams.IRandomAccessStream stream)
        {
            // Touch nothing: the real one reads the stream, which the harness does not need to do.
            _ = ImageOpened;
            _ = ImageFailed;
            return Task.CompletedTask;
        }
    }
}

namespace Microsoft.UI.Dispatching
{
    /// <summary>Headless stand-in: no UI thread exists, so the queue is always null and the
    /// service runs the decode inline (exactly the branch the app never takes).</summary>
    public sealed class DispatcherQueue
    {
        public static DispatcherQueue? GetForCurrentThread() => null;

        public bool TryEnqueue(DispatcherQueueHandler callback) => false;
    }

    public delegate void DispatcherQueueHandler();
}
