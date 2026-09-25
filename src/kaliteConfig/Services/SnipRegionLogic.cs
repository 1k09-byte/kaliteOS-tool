using System;

namespace kaliteConfig.Services;

// UI-free capture logic for the snip overlay. Every rule here is unit-tested
// in tools/SnipVerify; the overlay window only forwards events to it.
// Units: bitmap/physical pixels unless a name says Dips.
public enum SnipCaptureState { Idle, Selecting, Selected, Closed }

public readonly struct SnipRect
{
    public readonly double X, Y, W, H;
    public SnipRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
    public double Area => W * H;
}

public enum SnipHit
{
    NewSelection,
    TopLeft, TopCenter, TopRight,
    MiddleLeft, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
    RootPan,
}

public static class SnipRegionLogic
{
    /// <summary>Press-to-release distance under which a press counts as a click (spec: ~3 px).</summary>
    public const double ClickThresholdPx = 3.0;

    public static SnipRect Normalize(double ax, double ay, double bx, double by) =>
        new SnipRect(Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(bx - ax), Math.Abs(by - ay));

    public static SnipRect ClampToImage(SnipRect r, double imgW, double imgH)
    {
        double x = Math.Max(0, Math.Min(r.X, imgW));
        double y = Math.Max(0, Math.Min(r.Y, imgH));
        double w = Math.Max(0, Math.Min(r.X + r.W, imgW) - x);
        double h = Math.Max(0, Math.Min(r.Y + r.H, imgH) - y);
        return new SnipRect(x, y, w, h);
    }

    public static bool IsClick(double pressX, double pressY, double releaseX, double releaseY) =>
        Math.Abs(releaseX - pressX) < ClickThresholdPx && Math.Abs(releaseY - pressY) < ClickThresholdPx;

    /// <summary>Pure selection hit test on a normalized rect (x,y top-left).
    /// Pressing empty space outside always starts a new selection.</summary>
    public static SnipHit HitTestSelection(double x, double y, double w, double h,
        double px, double py, double grab)
    {
        if (w <= 0 || h <= 0) return SnipHit.NewSelection;
        bool Hit(double hx, double hy) => Math.Abs(px - hx) <= grab && Math.Abs(py - hy) <= grab;
        if (Hit(x, y)) return SnipHit.TopLeft;
        if (Hit(x + w / 2, y)) return SnipHit.TopCenter;
        if (Hit(x + w, y)) return SnipHit.TopRight;
        if (Hit(x, y + h / 2)) return SnipHit.MiddleLeft;
        if (Hit(x + w, y + h / 2)) return SnipHit.MiddleRight;
        if (Hit(x, y + h)) return SnipHit.BottomLeft;
        if (Hit(x + w / 2, y + h)) return SnipHit.BottomCenter;
        if (Hit(x + w, y + h)) return SnipHit.BottomRight;
        if (px >= x && px <= x + w && py >= y && py <= y + h) return SnipHit.RootPan;
        return SnipHit.NewSelection;
    }

    public static double DipsToPhysical(double dips, double scale) => dips * scale;
    public static double PhysicalToDips(double px, double scale) => scale > 0 ? px / scale : px;

    /// <summary>Region selection ignores Shift, Alt and Ctrl entirely (spec).</summary>
    public static bool RegionIgnoresModifiers() => true;

    /// <summary>Constraint modifiers apply ONLY to shape annotation tools, read live at
    /// each pointer event, and never while the modifier is still held over from the hotkey.</summary>
    public static bool ShouldSquareShape(bool toolIsShape, bool shiftLiveDown, bool shiftWasPreheld) =>
        toolIsShape && shiftLiveDown && !shiftWasPreheld;

    public static SnipRect ApplySquare(double startX, double startY, double curX, double curY)
    {
        double dx = curX - startX, dy = curY - startY;
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return Normalize(startX, startY, startX + Math.Sign(dx) * side, startY + Math.Sign(dy) * side);
    }

    /// <summary>Tracks modifiers physically held from the hotkey: they are ignored
    /// until each one has been observed released at least once.</summary>
    public sealed class ModifierHoldTracker
    {
        private bool _shiftPreheld, _ctrlPreheld, _altPreheld;
        public void CapturePreheld(bool shiftDown, bool ctrlDown, bool altDown)
        {
            _shiftPreheld = shiftDown; _ctrlPreheld = ctrlDown; _altPreheld = altDown;
        }
        public void ObserveLive(bool shiftDown, bool ctrlDown, bool altDown)
        {
            if (!shiftDown) _shiftPreheld = false;
            if (!ctrlDown) _ctrlPreheld = false;
            if (!altDown) _altPreheld = false;
        }
        public bool ShiftPreheld => _shiftPreheld;
        public bool CtrlPreheld => _ctrlPreheld;
        public bool AltPreheld => _altPreheld;
    }

    public static string BuildSnipFileName(DateTime local, string extension = ".png")
    {
        if (!extension.StartsWith(".")) extension = "." + extension;
        return $"Snip_{local:yyyyMMdd_HHmmss_fff}{extension}";
    }

    /// <summary>Builds CF_DIB bytes (BITMAPINFOHEADER + bottom-up BGR rows) for clipboard paste
    /// into Paint/Word/Discord alongside the PNG.</summary>
    public static byte[] BuildDib32(byte[] bgraTopDown, int width, int height)
    {
        if (bgraTopDown == null) throw new ArgumentNullException(nameof(bgraTopDown));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException();
        if (bgraTopDown.Length < width * height * 4) throw new ArgumentException("Pixel buffer too small.");
        var dib = new byte[40 + width * height * 4];
        void W16(int off, int v) { dib[off] = (byte)(v & 0xFF); dib[off + 1] = (byte)((v >> 8) & 0xFF); }
        void W32(int off, int v) { W16(off, v & 0xFFFF); W16(off + 2, (v >> 16) & 0xFFFF); }
        W32(0, 40); W32(4, width); W32(8, height); W16(12, 1); W16(14, 32); W32(16, 0); W32(20, width * height * 4);
        W32(24, 2835); W32(28, 2835); W32(32, 0); W32(36, 0);
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(bgraTopDown, y * width * 4, dib, 40 + (height - 1 - y) * width * 4, width * 4);
        return dib;
    }
}
