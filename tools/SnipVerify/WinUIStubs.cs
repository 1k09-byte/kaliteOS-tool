using System.Threading.Tasks;

// Minimal stand-in so the REAL SnipGalleryService.cs compiles in this
// headless harness. BitmapImage is a XAML object; IO tests never create one.
namespace Microsoft.UI.Xaml.Media.Imaging
{
    public sealed class BitmapImage
    {
        public int DecodePixelWidth { get; set; }

        public Task SetSourceAsync(Windows.Storage.Streams.IRandomAccessStream stream)
            => Task.CompletedTask;
    }
}
