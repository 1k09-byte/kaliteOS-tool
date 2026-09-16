using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

// Usage: icongen <sourcePng> <outDir>
// Generates: kaliteConfig.ico (256/48/32/16 PNG entries), AppIcon.png (256),
// Square44x44Logo.png, Square150x150Logo.png, Wide310x150Logo.png,
// StoreLogo.png (50), SplashScreen.png (620x300), LockScreenLogo.png (48).
if (args.Length < 2) { Console.WriteLine("usage: icongen <src> <outdir>"); return 1; }
string src = args[0];
string outDir = args[1];
Directory.CreateDirectory(outDir);

static async Task<(byte[] rgba, uint w, uint h)> Decode(string path, uint? tw = null, uint? th = null, bool key = true)
{
    using var stream = File.OpenRead(path).AsRandomAccessStream();
    var decoder = await BitmapDecoder.CreateAsync(stream);
    var transform = new BitmapTransform();
    if (tw.HasValue) transform.ScaledWidth = tw.Value;
    if (th.HasValue) transform.ScaledHeight = th.Value;
    transform.InterpolationMode = BitmapInterpolationMode.Fant;
    var pixels = await decoder.GetPixelDataAsync(
        BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied,
        transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
    var bmp = await decoder.GetSoftwareBitmapAsync(
        BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied,
        transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
    var data = pixels.DetachPixelData();
    uint pw = (uint)bmp.PixelWidth, ph = (uint)bmp.PixelHeight;
    if (key) KeyOutBlack(data, pw, ph);
    return (data, pw, ph);
}

// The source art sits on a baked-in black square. Flood-fill from the image
// edges through PURE black only (<=10): clears the outer surround while the
// dark glass interior (brighter than pure black) is fully preserved.
static void KeyOutBlack(byte[] rgba, uint w, uint h)
{
    const int thresh = 16;
    int W = (int)w, H = (int)h;
    var seen = new bool[W * H];
    var stack = new Stack<int>();
    for (int x = 0; x < W; x++) { stack.Push(x); stack.Push((H - 1) * W + x); }
    for (int y = 0; y < H; y++) { stack.Push(y * W); stack.Push(y * W + W - 1); }
    static bool NearBlack(byte[] d, int p) => d[p] <= thresh && d[p + 1] <= thresh && d[p + 2] <= thresh;
    while (stack.Count > 0)
    {
        int p = stack.Pop();
        if (p < 0 || p >= W * H || seen[p]) continue;
        seen[p] = true;
        int o = p * 4;
        if (!NearBlack(rgba, o)) continue;
        rgba[o] = rgba[o + 1] = rgba[o + 2] = rgba[o + 3] = 0;
        int x = p % W, y = p / W;
        if (x > 0) stack.Push(p - 1);
        if (x < W - 1) stack.Push(p + 1);
        if (y > 0) stack.Push(p - W);
        if (y < H - 1) stack.Push(p + W);
    }
}

// Crop to the non-transparent bounding box plus padding, so the mark fills
// the icon instead of floating in leftover margins.
static (byte[] rgba, uint w, uint h) CropToContent(byte[] rgba, uint w, uint h)
{
    int W = (int)w, H = (int)h;
    int x0 = W, y0 = H, x1 = -1, y1 = -1;
    for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
            if (rgba[(y * W + x) * 4 + 3] > 8)
            { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
    if (x1 < x0) return (rgba, w, h);
    int pad = (int)((x1 - x0 + y1 - y0) / 2 * 0.07);
    x0 = Math.Max(0, x0 - pad); y0 = Math.Max(0, y0 - pad);
    x1 = Math.Min(W - 1, x1 + pad); y1 = Math.Min(H - 1, y1 + pad);
    int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
    int side = Math.Max(cw, ch);
    var canvas = new byte[side * side * 4];
    int ox = (side - cw) / 2, oy = (side - ch) / 2;
    for (int y = 0; y < ch; y++)
        System.Buffer.BlockCopy(rgba, ((y0 + y) * W + x0) * 4, canvas, ((oy + y) * side + ox) * 4, cw * 4);
    return (canvas, (uint)side, (uint)side);
}

static async Task<byte[]> EncodePng(byte[] rgba, uint w, uint h)
{
    using var ms = new InMemoryRandomAccessStream();
    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
    encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied, w, h, 96, 96, rgba);
    await encoder.FlushAsync();
    var buf = new byte[ms.Size];
    await ms.AsStreamForRead().ReadExactlyAsync(buf);
    return buf;
}

// Center src onto a transparent WxH canvas.
static byte[] Composite(byte[] src, uint sw, uint sh, uint cw, uint ch)
{
    var canvas = new byte[cw * ch * 4];
    uint ox = (cw - sw) / 2, oy = (ch - sh) / 2;
    for (uint y = 0; y < sh; y++)
        System.Buffer.BlockCopy(src, (int)(y * sw * 4), canvas, (int)(((oy + y) * cw + ox) * 4), (int)(sw * 4));
    return canvas;
}

static void WriteIco(string path, List<(byte[] png, uint size)> entries)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)entries.Count);
    int offset = 6 + 16 * entries.Count;
    foreach (var (png, size) in entries)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write((uint)png.Length); w.Write((uint)offset);
        offset += png.Length;
    }
    foreach (var (png, _) in entries) w.Write(png);
}

var (full, fw, fh) = await Decode(src);
Console.WriteLine($"source: {fw}x{fh}");
{ // border brightness probe (post-key: residual border shows a too-low threshold)
    int W = (int)fw, H = (int)fh, max = 0;
    long sum = 0; int n = 0;
    void Sample(int x, int y) { int o = (y * W + x) * 4; if (full[o + 3] < 8) return; int m = Math.Max(full[o], Math.Max(full[o+1], full[o+2])); if (m > max) max = m; sum += m; n++; }
    for (int x = 0; x < W; x += 7) { Sample(x, 0); Sample(x, H - 1); }
    for (int y = 0; y < H; y += 7) { Sample(0, y); Sample(W - 1, y); }
    Console.WriteLine(n == 0 ? "border: fully cleared" : $"border remaining: max={max} avg={sum / n}");
}
var (cropped, cw, ch) = CropToContent(full, fw, fh);
Console.WriteLine($"cropped: {cw}x{ch}");
// Re-decode scaled sizes from the cropped, keyed art.
string work = Path.Combine(outDir, "_work.png");
await File.WriteAllBytesAsync(work, await EncodePng(cropped, cw, ch));
async Task<(byte[] rgba, uint w, uint h)> Scaled(uint s) => await Decode(work, s, s, false);

// Square sizes (icon content scaled to fit; source is already square art).
var png256 = await EncodePng((await Scaled(256)).rgba, 256, 256);
var png48 = await EncodePng((await Scaled(48)).rgba, 48, 48);
var png32 = await EncodePng((await Scaled(32)).rgba, 32, 32);
var png16 = await EncodePng((await Scaled(16)).rgba, 16, 16);
WriteIco(Path.Combine(outDir, "kaliteConfig.ico"),
    new() { (png256, 256), (png48, 48), (png32, 32), (png16, 16) });

await File.WriteAllBytesAsync(Path.Combine(outDir, "AppIcon.png"), png256);
await File.WriteAllBytesAsync(Path.Combine(outDir, "Square44x44Logo.png"),
    await EncodePng((await Scaled(44)).rgba, 44, 44));
await File.WriteAllBytesAsync(Path.Combine(outDir, "Square150x150Logo.png"),
    await EncodePng((await Scaled(150)).rgba, 150, 150));
await File.WriteAllBytesAsync(Path.Combine(outDir, "StoreLogo.png"),
    await EncodePng((await Scaled(50)).rgba, 50, 50));
await File.WriteAllBytesAsync(Path.Combine(outDir, "LockScreenLogo.png"),
    await EncodePng((await Scaled(48)).rgba, 48, 48));

// Wide tile + splash: icon fitted, centered on transparent canvas.
var (fit150, w150, h150) = await Scaled(150);
await File.WriteAllBytesAsync(Path.Combine(outDir, "Wide310x150Logo.png"),
    await EncodePng(Composite(fit150, w150, h150, 310, 150), 310, 150));
var (fit256b, w2, h2) = await Scaled(256);
await File.WriteAllBytesAsync(Path.Combine(outDir, "SplashScreen.png"),
    await EncodePng(Composite(fit256b, w2, h2, 620, 300), 620, 300));

Console.WriteLine("done");
File.Delete(work);
return 0;
