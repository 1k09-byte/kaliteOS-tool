using System;

namespace kaliteConfig.Services;

public static class SnipRelativeTime
{
    public static string Format(string filePath)
    {
        try
        {
            var created = System.IO.File.GetCreationTimeUtc(filePath);
            return Format(created);
        }
        catch
        {
            return "";
        }
    }

    public static string Format(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} d ago";
        return utc.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
}
