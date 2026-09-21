using System;
using kaliteConfig.Services;

// Unit tests for the resize dimension math (SnipGalleryQuery.ComputeResizeDims).
internal static class ResizeTests
{
    public static void Run(Action<bool, string> check)
    {
        var r = SnipGalleryQuery.ComputeResizeDims(800, 600, 400, 0, true);
        check(r == (400, 300), "resize locked width scales height");
        r = SnipGalleryQuery.ComputeResizeDims(800, 600, 0, 300, true);
        check(r == (400, 300), "resize locked height scales width");
        r = SnipGalleryQuery.ComputeResizeDims(800, 600, 400, 400, true);
        check(r == (400, 300), "resize locked both fits inside");
        r = SnipGalleryQuery.ComputeResizeDims(800, 600, 400, 100, false);
        check(r == (400, 100), "resize unlocked is exact");
        r = SnipGalleryQuery.ComputeResizeDims(800, 600, 0, 0, true);
        check(r == (0, 0), "resize all-auto is a no-op");
        r = SnipGalleryQuery.ComputeResizeDims(800, 600, 0, 0, false);
        check(r == (0, 0), "resize all-auto unlocked is a no-op");
        r = SnipGalleryQuery.ComputeResizeDims(0, 600, 400, 0, true);
        check(r == (0, 0), "resize degenerate source rejected");
        r = SnipGalleryQuery.ComputeResizeDims(800, 600, 2000, 0, true);
        check(r == (2000, 1500), "resize upscale keeps ratio");
    }
}
