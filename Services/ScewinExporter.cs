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
///  - Modified items keep every line except the "Value" line, whose right-hand
///    side is replaced; the "Value …=" prefix spacing is preserved.
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
                // then a single space and the new value token.
                sb.Append(eq >= 0 ? content[..(eq + 1)] : content)
                  .Append(' ')
                  .Append(item.Value.Trim())
                  .Append(ending);
            }
            else
            {
                sb.Append(content).Append(ending);
            }
        }
        return sb.ToString();
    }
}
