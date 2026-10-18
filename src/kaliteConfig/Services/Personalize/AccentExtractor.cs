using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace kaliteConfig.Services.Personalize;

public sealed record AccentExtraction(int Argb, double Luminance, int DistinctColors, int SampleCount);

public static class AccentExtractor
{
    private const int SampleWidth = 96;

    public static AccentExtraction Extract(string imagePath)
    {
        using var source = new Bitmap(imagePath);

        int width = Math.Min(source.Width, SampleWidth * 4);
        int height = Math.Max(1, (int)Math.Round(width * (double)source.Height / Math.Max(1, source.Width)));

        using var scaled = new Bitmap(width, height);
        using (Graphics g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        var counts = new Dictionary<int, int>();
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                Color c = scaled.GetPixel(x, y);
                if (c.A < 128) continue;
                int rgb = (c.R << 16) | (c.G << 8) | c.B;
                int key = (((rgb >> 19) & 0x1F) << 10) | (((rgb >> 11) & 0x1F) << 5) | ((rgb >> 3) & 0x1F);
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }
        }

        if (counts.Count == 0) return new AccentExtraction(unchecked((int)0xFF0078D4), 0.5, 0, 0);

        var buckets = counts
            .Select(kv => (Key: Expand(kv.Key), Count: kv.Value))
            .GroupBy(b => b.Key)
            .Select(g => (Key: g.Key, Count: g.Sum(b => b.Count)))
            .OrderByDescending(b => b.Count)
            .ToList();

        int best = PickAccent(buckets);
        double luminance = Luminance(best);
        int samples = buckets.Sum(b => b.Count);
        return new AccentExtraction(unchecked((int)(0xFF000000 | (uint)best)), luminance, buckets.Count, samples);
    }

    private static int Expand(int key)
    {
        int r = ((key >> 10) & 0x1F) << 3;
        int g = ((key >> 5) & 0x1F) << 3;
        int b = (key & 0x1F) << 3;
        return (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);
    }

    private static int PickAccent(List<(int Key, int Count)> buckets)
    {
        double total = buckets.Sum(b => (double)b.Count);
        var scored = new List<(int Color, double Score)>();

        foreach (var (key, count) in buckets)
        {
            int r = (key >> 16) & 0xFF;
            int g = (key >> 8) & 0xFF;
            int b = key & 0xFF;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double sat = max == 0 ? 0 : (max - min) / max;
            double lum = Luminance(key);
            double share = count / total;

            if (lum < 0.18 || lum > 0.88) continue;
            if (max < 70) continue;

            double score = share * (0.25 + sat * 1.70);
            score *= 1.0 - Math.Abs(lum - 0.52);
            if (sat < 0.20) score *= 0.25;

            scored.Add((key, score));
        }

        if (scored.Count == 0) return Fallback(buckets);
        return scored.OrderByDescending(s => s.Score).First().Color;
    }

    private static int Fallback(List<(int Key, int Count)> buckets)
    {
        var candidates = buckets
            .Where(b => Luminance(b.Key) >= 0.18 && Luminance(b.Key) <= 0.88)
            .OrderByDescending(b => b.Count)
            .ToList();

        if (candidates.Count == 0) return 0x0078D4;

        int sumR = 0, sumG = 0, sumB = 0, total = 0;
        foreach (var (key, count) in candidates.Take(6))
        {
            sumR += ((key >> 16) & 0xFF) * count;
            sumG += ((key >> 8) & 0xFF) * count;
            sumB += (key & 0xFF) * count;
            total += count;
        }

        if (total == 0) return 0x0078D4;
        return ((sumR / total) << 16) | ((sumG / total) << 8) | (sumB / total);
    }

    private static double Luminance(int rgb)
    {
        double r = ((rgb >> 16) & 0xFF) / 255.0;
        double g = ((rgb >> 8) & 0xFF) / 255.0;
        double b = (rgb & 0xFF) / 255.0;
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }
}
