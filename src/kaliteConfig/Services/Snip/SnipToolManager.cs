// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace kaliteConfig.Services;

public class SnipToolManager
{
    public static async Task<SoftwareBitmap?> IngestFromClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap)) return null;

            var streamRef = await content.GetBitmapAsync();
            using var stream = await streamRef.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            
            return softwareBitmap;
        }
        catch { return null; }
    }

    public static async Task<SoftwareBitmap?> IngestFromFileAsync(StorageFile file)
    {
        try
        {
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            return softwareBitmap;
        }
        catch { return null; }
    }

    // Typical PII patterns
    private static readonly Regex EmailRegex = new Regex(@"[a-zA-Z0-9_.+-]+@[a-zA-Z0-9-]+\.[a-zA-Z0-9-.]+", RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new Regex(@"\+?\d{1,3}?[- .]?\(?(?:\d{2,3})\)?[- .]?\d\d\d[- .]?\d\d\d\d", RegexOptions.Compiled);
    private static readonly Regex IpRegex = new Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled);
    
    public static async Task<List<Windows.Foundation.Rect>> CalculateRedactionBoundsAsync(SoftwareBitmap bitmap)
    {
        var targets = new List<Windows.Foundation.Rect>();
        var ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (ocrEngine == null) return targets;

        var result = await ocrEngine.RecognizeAsync(bitmap);
        if (result == null || string.IsNullOrWhiteSpace(result.Text)) return targets;

        // Flatten all words into a single list while preserving a contiguous string builder
        System.Text.StringBuilder sb = new();
        List<(int StartIndex, Windows.Foundation.Rect Bounds)> charToRectMap = new();

        int currentIndex = 0;
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                string token = word.Text;
                sb.Append(token);
                // Map every character in this word to its bounding box
                for (int i = 0; i < token.Length; i++)
                {
                    charToRectMap.Add((currentIndex + i, word.BoundingRect));
                }
                currentIndex += token.Length;

                // Add a space after words to support regex matching correctly
                sb.Append(" ");
                charToRectMap.Add((currentIndex, Windows.Foundation.Rect.Empty)); // space has no bounds
                currentIndex++;
            }
            sb.AppendLine();
            currentIndex += Environment.NewLine.Length; // Add padding for newline
        }

        string fullText = sb.ToString();

        // Target matchers
        void processRegexMatches(Regex r)
        {
            foreach (Match m in r.Matches(fullText))
            {
                // We need to union the bounding rects of all matched characters
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                bool hasBounds = false;

                for (int i = 0; i < m.Length; i++)
                {
                    int idx = m.Index + i;
                    var mapping = charToRectMap.FirstOrDefault(c => c.StartIndex == idx);
                    if (mapping.Bounds != Windows.Foundation.Rect.Empty)
                    {
                        var b = mapping.Bounds;
                        if (b.Left < minX) minX = b.Left;
                        if (b.Top < minY) minY = b.Top;
                        if (b.Right > maxX) maxX = b.Right;
                        if (b.Bottom > maxY) maxY = b.Bottom;
                        hasBounds = true;
                    }
                }

                if (hasBounds)
                {
                    // Expand slightly for visual padding
                    targets.Add(new Windows.Foundation.Rect(minX - 2, minY - 2, (maxX - minX) + 4, (maxY - minY) + 4));
                }
            }
        }

        processRegexMatches(EmailRegex);
        processRegexMatches(PhoneRegex);
        processRegexMatches(IpRegex);

        return targets;
    }
}

