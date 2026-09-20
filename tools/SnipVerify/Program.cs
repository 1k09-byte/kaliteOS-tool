using System;
using System.Collections.Generic;
using System.Linq;
using kaliteConfig.Models;
using kaliteConfig.Services;

internal sealed class Program
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool cond, string label)
    {
        if (cond) { _pass++; Console.WriteLine($"PASS  {label}"); }
        else { _fail++; Console.WriteLine($"FAIL  {label}"); }
    }

    private static SnipEntry Mk(string name, long size, DateTime createdUtc, string type = "Region",
        string sourceApp = "", string ocr = "", bool fav = false, List<string>? tags = null, SnipFormat fmt = SnipFormat.Png)
    {
        return new SnipEntry
        {
            FilePath = $@"C:\snips\{name}",
            Name = name,
            Extension = name[name.LastIndexOf('.')..],
            Format = fmt,
            SizeBytes = size,
            CreatedUtc = createdUtc,
            Type = type,
            SourceApp = sourceApp,
            OcrText = ocr,
            Tags = tags ?? new List<string>(),
            IsFavorite = fav,
        };
    }

    private static async System.Threading.Tasks.Task<int> Main()
    {
        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var e1 = Mk("pink.png", 1500, now.AddMinutes(-5), "Region", "chrome.exe", "Hello world");
        var e2 = Mk("notes.jpg", 120000, now.AddDays(-1), "Window", "notepad.exe", "", fav: true, fmt: SnipFormat.Jpeg);
        var e3 = Mk("chart.webp", 500, now.AddDays(-10), "Fullscreen", "chrome.exe", "", tags: new List<string> { "chart", "docs" }, fmt: SnipFormat.Webp);
        var e4 = Mk("old.bmp", 900000, now.AddDays(-40), "Region", "paint.exe", "figure 3");
        var all = new List<SnipEntry> { e1, e2, e3, e4 };

        // FormatFromExtension
        Check(SnipGalleryQuery.FormatFromExtension(".PNG") == SnipFormat.Png, "FormatFromExtension .PNG -> Png");
        Check(SnipGalleryQuery.FormatFromExtension(".jpeg") == SnipFormat.Jpeg, "FormatFromExtension .jpeg -> Jpeg");
        Check(SnipGalleryQuery.FormatFromExtension(".webp") == SnipFormat.Webp, "FormatFromExtension .webp -> Webp");
        Check(SnipGalleryQuery.FormatFromExtension(".txt") == SnipFormat.Unknown, "FormatFromExtension .txt -> Unknown");

        // Unique source apps
        var apps = SnipGalleryQuery.DistinctSourceApps(all);
        Check(apps.Count == 3 && apps.Contains("chrome.exe"), "DistinctSourceApps dedupes chrome");
        Check(!apps.Contains(""), "DistinctSourceApps skips empty source");

        // Matches: plain All
        Check(SnipGalleryQuery.Matches(e1, new SnipFilterSpec(), now), "All filter matches any");
        Check(!SnipGalleryQuery.Matches(e1, new SnipFilterSpec { FavoritesOnly = true }, now), "FavoritesOnly excludes non-fav");
        Check(SnipGalleryQuery.Matches(e2, new SnipFilterSpec { FavoritesOnly = true }, now), "FavoritesOnly keeps fav");

        // Query search: filename, source app, OCR text, tags, type
        Check(SnipGalleryQuery.Matches(e1, new SnipFilterSpec { Query = "pink" }, now), "Query matches filename");
        Check(SnipGalleryQuery.Matches(e2, new SnipFilterSpec { Query = "notepad" }, now), "Query matches source app");
        Check(SnipGalleryQuery.Matches(e4, new SnipFilterSpec { Query = "figure" }, now), "Query matches OCR text");
        Check(SnipGalleryQuery.Matches(e3, new SnipFilterSpec { Query = "docs" }, now), "Query matches tag");
        Check(SnipGalleryQuery.Matches(e1, new SnipFilterSpec { Query = "region" }, now), "Query matches type");
        Check(!SnipGalleryQuery.Matches(e3, new SnipFilterSpec { Query = "zzz" }, now), "Query no-match");

        // Type filter
        Check(SnipGalleryQuery.Matches(e2, new SnipFilterSpec { Type = "Window" }, now), "Type filter Window");
        Check(!SnipGalleryQuery.Matches(e2, new SnipFilterSpec { Type = "Region" }, now), "Type filter Region excludes Window");

        // Format filter (case insensitive prefix)
        Check(SnipGalleryQuery.Matches(e3, new SnipFilterSpec { Format = "WebP" }, now), "Format filter WebP");
        Check(SnipGalleryQuery.Matches(e2, new SnipFilterSpec { Format = "JPEG" }, now), "Format filter JPEG");
        Check(!SnipGalleryQuery.Matches(e2, new SnipFilterSpec { Format = "PNG" }, now), "Format filter PNG excludes jpg");

        // Source app filter
        Check(SnipGalleryQuery.Matches(e1, new SnipFilterSpec { SourceApp = "chrome.exe" }, now), "SourceApp filter chrome");
        Check(!SnipGalleryQuery.Matches(e4, new SnipFilterSpec { SourceApp = "chrome.exe" }, now), "SourceApp filter excludes paint");

        // Date filters
        Check(SnipGalleryQuery.Matches(e1, new SnipFilterSpec { Date = SnipDateFilter.Today }, now), "Date Today matches recent");
        Check(!SnipGalleryQuery.Matches(e2, new SnipFilterSpec { Date = SnipDateFilter.Today }, now), "Date Today excludes yesterday");
        Check(SnipGalleryQuery.Matches(e2, new SnipFilterSpec { Date = SnipDateFilter.Last7 }, now), "Date Last7 keeps yesterday");
        Check(!SnipGalleryQuery.Matches(e3, new SnipFilterSpec { Date = SnipDateFilter.Last7 }, now), "Date Last7 drops 10d");
        Check(SnipGalleryQuery.Matches(e3, new SnipFilterSpec { Date = SnipDateFilter.Last30 }, now), "Date Last30 keeps 10d");
        Check(!SnipGalleryQuery.Matches(e4, new SnipFilterSpec { Date = SnipDateFilter.Last30 }, now), "Date Last30 drops 40d");
        Check(SnipGalleryQuery.Matches(e3, new SnipFilterSpec { Date = SnipDateFilter.ThisMonth }, now), "Date ThisMonth keeps within month");
        Check(!SnipGalleryQuery.Matches(e4, new SnipFilterSpec { Date = SnipDateFilter.ThisMonth }, now), "Date ThisMonth drops previous month");

        // Sorting
        var sortedNewest = SnipGalleryQuery.Apply(all, new SnipFilterSpec(), SnipSortMode.Newest);
        Check(sortedNewest[0] == e1, "Sort Newest is e1");
        sortedNewest = SnipGalleryQuery.Apply(all, new SnipFilterSpec(), SnipSortMode.Oldest);
        Check(sortedNewest[0] == e4, "Sort Oldest is e4");
        var sortedSizeDesc = SnipGalleryQuery.Apply(all, new SnipFilterSpec(), SnipSortMode.SizeDesc);
        Check(sortedSizeDesc[0] == e4 && sortedSizeDesc[3] == e3, "Sort SizeDesc order");
        var sortedName = SnipGalleryQuery.Apply(all, new SnipFilterSpec(), SnipSortMode.NameAsc);
        Check(sortedName[0].Name == "chart.webp", "Sort NameAsc first");

        // Combined filter (favorites, chrome, region, PNG, last 30 days, query "hello")
        var combo = SnipGalleryQuery.Apply(all, new SnipFilterSpec
        {
            FavoritesOnly = true,
            SourceApp = "chrome.exe",
            Type = "Region",
            Format = "PNG",
            Date = SnipDateFilter.Today,
            Query = "hello",
        }, SnipSortMode.Newest);
        Check(combo.Count == 0, "Combined filter yields 0 (fav excludes e1)");

        // Combined filter without favorites -> e1
        var combo2 = SnipGalleryQuery.Apply(all, new SnipFilterSpec
        {
            SourceApp = "chrome.exe",
            Type = "Region",
            Format = "PNG",
            Query = "hello",
        }, SnipSortMode.Newest);
        Check(combo2.Count == 1 && combo2[0] == e1, "Combined filter isolates e1");

        // SafeFileName
        Check(SnipGalleryQuery.SafeFileName("My Snip", ".png") == "My Snip.png", "SafeFileName appends extension");
        Check(SnipGalleryQuery.SafeFileName("a<b>c", ".png") == "a_b_c.png", "SafeFileName strips invalid chars");
        Check(SnipGalleryQuery.SafeFileName("", ".png") == "Snip.png", "SafeFileName empty fallback");
        Check(SnipGalleryQuery.SafeFileName("   ", ".png") == "Snip.png", "SafeFileName whitespace fallback");
        Check(SnipGalleryQuery.SafeFileName("already.png", ".png") == "already.png", "SafeFileName keeps existing ext");

        // FormatSize
        Check(SnipGalleryQuery.FormatSize(512) == "512 B", "FormatSize bytes");
        Check(SnipGalleryQuery.FormatSize(2048) == "2.0 KB", "FormatSize KB");
        Check(SnipGalleryQuery.FormatSize(1536L * 1024) == "1.50 MB", "FormatSize MB");

        Console.WriteLine();
        Console.WriteLine("--- glyph coverage (segmdl2.ttf cmap) ---");
        GlyphTests.Run(Check);

        Console.WriteLine();
        Console.WriteLine("--- custom hotkeys ---");
        HotkeyTests.Run(Check);

        Console.WriteLine();
        Console.WriteLine("--- capture region logic ---");
        RegionLogicTests.Run(Check);

        Console.WriteLine();
        Console.WriteLine("--- gallery disk round-trip (real service) ---");
        await GalleryIoTests.RunAsync(Check);

        Console.WriteLine();
        Console.WriteLine("--- phase 3: large preview (viewport policy + progressive load) ---");
        await PreviewPipelineTests.RunAsync(Check);
        await FolderWatchTests.RunAsync(Check);

        Console.WriteLine();
        Console.WriteLine($"SnipVerify: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}