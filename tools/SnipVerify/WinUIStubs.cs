using System;
using System.Threading.Tasks;

namespace Microsoft.UI.Xaml
{
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

        public int PixelWidth { get; private set; } = 1;

        public int PixelHeight { get; private set; } = 1;

        public Task SetSourceAsync(Windows.Storage.Streams.IRandomAccessStream stream)
        {
            _ = ImageOpened;
            _ = ImageFailed;
            return Task.CompletedTask;
        }
    }
}

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public static DispatcherQueue? GetForCurrentThread() => null;

        public bool TryEnqueue(DispatcherQueueHandler callback) => false;
    }

    public delegate void DispatcherQueueHandler();
}
