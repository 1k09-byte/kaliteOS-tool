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
using System.Text;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

/// <summary>
/// Rebuilds SCEWIN dump text from a <see cref="ScewinDocument"/>.
///
/// Fidelity rules:
///  - Unmodified items are written back VERBATIM (their raw block text).
///  - Items WITH an explicit "Value =" line keep every line except that one,
///    whose right-hand side is replaced; spacing is preserved.
///  - Items WITHOUT a Value line (starred-option script dumps): the current
///    value is expressed by the "*" marker in the Options list. Export moves
///    the star to the option matching the new value. The Options line is the
///    edit point; unmodified items still round-trip byte-identically.
///  - Headers, comments, separators and blank lines are always verbatim.
///
/// Therefore import → export with zero edits is byte-identical, and an edited
/// file differs from the original only on the edited "Value" lines.
/// </summary>
public static class ScewinExporter
{
    public static string Export(ScewinDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var sb = new StringBuilder(document.RawText.Length + 64);
        foreach (var segment in document.Segments)
        {
            switch (segment)
            {
                case ItemSegment { Item: { } item }:
                    sb.Append(item.IsModified ? RewriteValueLine(item) : item.RawBlock);
                    break;
                case HeaderSegment header:
                    sb.Append(header.Raw);
                    break;
                case OtherSegment other:
                    sb.Append(other.Raw);
                    break;
                default:
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Items whose new value fails validation, with the reason. Non-blocking: the UI warns.</summary>
    public static List<(BiosSetting Item, string Message)> ValidateForExport(ScewinDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Items
            .Where(i => i.IsModified)
            .Select(i => (Item: i, Message: i.ValidateValue()))
            .Where(t => t.Message is not null)
            .Select(t => (t.Item, t.Message!))
            .ToList();
    }

    private static string RewriteValueLine(BiosSetting item)
    {
        var lines = ScewinParser.SplitLines(item.RawBlock);
        var sb = new StringBuilder(item.RawBlock.Length + 16);
        for (int i = 0; i < lines.Count; i++)
        {
            var (content, ending) = lines[i];
            if (i == item.ValueLineIndex)
            {
                int eq = content.IndexOf('=');
                // Keep everything through the '=' (spacing and key spelling),
                // then the new value token - preserving whether the original
                // had a space after '=' (AMISCE writes "Value\t=<550>").
                bool hadSpace = eq >= 0 && eq + 1 < content.Length && content[eq + 1] == ' ';
                sb.Append(eq >= 0 ? content[..(eq + 1)] : content);
                if (hadSpace) sb.Append(' ');
                sb.Append(item.Value.Trim()).Append(ending);
            }
            else if (!item.HasValueLine)
            {
                // Starred-option item: move the "*" marker to the option that
                // matches the new value; drop it from all other lines.
                sb.Append(RewriteStarMarker(content, item)).Append(ending);
            }
            else
            {
                sb.Append(content).Append(ending);
            }
        }
        return sb.ToString();
    }

    /// <summary>Shifts the current-value "*" marker on one option line.</summary>
    private static string RewriteStarMarker(string content, BiosSetting item)
    {
        var trimmedStart = content.TrimStart();
        // The first option may live on the "Options\t=[00]Auto" line itself.
        bool isOptionsLine = trimmedStart.StartsWith("Options", StringComparison.OrdinalIgnoreCase);
        if (!isOptionsLine && !trimmedStart.StartsWith('[') && !trimmedStart.StartsWith("*["))
            return content;

        var work = isOptionsLine
            ? trimmedStart[(trimmedStart.IndexOf('=') + 1)..].TrimStart()
            : trimmedStart;
        var prefixLen = content.Length - work.Length;

        bool isStarred = work.StartsWith("*");
        var bare = isStarred ? work[1..] : work;
        if (!bare.StartsWith('[')) return content;
        int close = bare.IndexOf(']');
        if (close < 0) return content;
        var token = bare[1..close];

        bool shouldStar = string.Equals(token, item.Value.Trim(), StringComparison.OrdinalIgnoreCase);
        if (shouldStar == isStarred) return content; // already correct

        var prefix = content[..prefixLen];
        return shouldStar ? $"{prefix}*{bare}" : $"{prefix}{bare}";
    }
}
