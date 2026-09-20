using System;
using System.Collections.Generic;

namespace kaliteConfig.Models;

public enum SnipFormat
{
    Unknown,
    Png,
    Jpeg,
    Bmp,
    Webp,
}

public enum SnipSortMode
{
    Newest,
    Oldest,
    NameAsc,
    NameDesc,
    SizeDesc,
    SizeAsc,
}

public enum SnipDateFilter
{
    Any,
    Today,
    Last7,
    Last30,
    ThisMonth,
}

public static class SnipFormatLookup
{
    public static SnipFormat FromExtension(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".png" => SnipFormat.Png,
            ".jpg" or ".jpeg" => SnipFormat.Jpeg,
            ".bmp" => SnipFormat.Bmp,
            ".webp" => SnipFormat.Webp,
            _ => SnipFormat.Unknown,
        };
    }
}

public class SnipMeta
{
    public string Type { get; set; } = "Region";
    public string SourceApp { get; set; } = "";
    public string OcrText { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool IsFavorite { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime? CreatedUtc { get; set; }
}

public class SnipEntry
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public SnipFormat Format { get; set; } = SnipFormat.Unknown;
    public long SizeBytes { get; set; }
    public DateTime CreatedUtc { get; set; }

    public string Type { get; set; } = "Region";
    public string SourceApp { get; set; } = "";
    public string OcrText { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool IsFavorite { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public SnipMeta ToMeta() => new()
    {
        Type = Type,
        SourceApp = SourceApp,
        OcrText = OcrText,
        Tags = new List<string>(Tags),
        IsFavorite = IsFavorite,
        Width = Width,
        Height = Height,
        CreatedUtc = CreatedUtc,
    };

    public static SnipEntry FromMeta(string filePath, long sizeBytes, SnipMeta? meta, DateTime fallbackCreatedUtc)
    {
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        var e = new SnipEntry
        {
            FilePath = filePath,
            Name = System.IO.Path.GetFileName(filePath),
            Extension = ext,
            Format = SnipFormatLookup.FromExtension(ext),
            SizeBytes = sizeBytes,
            CreatedUtc = meta?.CreatedUtc ?? fallbackCreatedUtc,
        };
        if (meta != null)
        {
            e.Type = string.IsNullOrEmpty(meta.Type) ? "Region" : meta.Type;
            e.SourceApp = meta.SourceApp ?? "";
            e.OcrText = meta.OcrText ?? "";
            e.Tags = meta.Tags ?? new List<string>();
            e.IsFavorite = meta.IsFavorite;
            e.Width = meta.Width;
            e.Height = meta.Height;
        }
        return e;
    }
}

public sealed class SnipFilterSpec
{
    public string Query { get; set; } = "";
    public string Type { get; set; } = "All";
    public string Format { get; set; } = "All";
    public string SourceApp { get; set; } = "All";
    public bool FavoritesOnly { get; set; }
    public SnipDateFilter Date { get; set; } = SnipDateFilter.Any;
}