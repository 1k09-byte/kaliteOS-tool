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
using System.Globalization;
using System.Linq;
using System.Text;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    public static class OverclockLogFilter
    {
        public static OverclockChangeResult? ResultFromIndex(int index)
            => index <= 0 ? null : (OverclockChangeResult)(index - 1);

        public static OverclockChangeSource? SourceFromIndex(int index)
            => index <= 0 ? null : (OverclockChangeSource)(index - 1);

        public static bool Matches(
            AppliedChangeLogEntry entry,
            OverclockChangeResult? result,
            OverclockChangeSource? source)
            => (result is null || entry.Result == result)
               && (source is null || entry.Source == source);
    }

    public static class OverclockVerificationExporter
    {
        public static string SuggestedFileName(DateTime when)
            => $"kaliteConfig-overclock-verification-{when:yyyy-MM-dd}.md";

        public static string BuildMarkdown(
            IReadOnlyList<AppliedChangeLogEntry> entries,
            string? gpuName = null,
            string? driverVersion = null,
            DateTime? generatedAt = null)
        {
            var at = (generatedAt ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();

            sb.AppendLine("# GPU Overclock Module - Verification Record");
            sb.AppendLine();
            sb.Append("Generated: ").Append(at);
            if (!string.IsNullOrWhiteSpace(gpuName)) sb.Append(" · GPU: ").Append(gpuName);
            if (!string.IsNullOrWhiteSpace(driverVersion)) sb.Append(" · driver ").Append(driverVersion);
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Source: in-app change log (`gpu-overclock-changes.log`), most recent entries.")
              .AppendLine("Every row is one audited driver write made by this app, including reverts and refusals.")
              .AppendLine();

            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| Result | Count |");
            sb.AppendLine("|---|---|");
            foreach (OverclockChangeResult r in Enum.GetValues(typeof(OverclockChangeResult)))
                sb.Append("| ").Append(r).Append(" | ")
                  .Append(entries.Count(e => e.Result == r)).Append(" |");
            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine("## Changes");
            sb.AppendLine();
            if (entries.Count == 0)
            {
                sb.AppendLine("No changes recorded.");
            }
            else
            {
                sb.AppendLine("| Time | Control | Change | Source | Result | Detail |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var e in entries.OrderBy(e => e.Timestamp))
                {
                    sb.Append("| ").Append(Cell(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                    sb.Append(" | ").Append(Cell(e.ControlName));
                    sb.Append(" | ").Append(Cell(ChangeDisplay(e)));
                    sb.Append(" | ").Append(Cell(e.Source.ToString()));
                    sb.Append(" | ").Append(Cell(e.Result.ToString()));
                    sb.Append(" | ").Append(Cell(e.FailureReason ?? ""));
                    sb.AppendLine(" |");
                }
            }

            return sb.ToString();
        }

        private static string ChangeDisplay(AppliedChangeLogEntry e)
            => string.Equals(e.OldValue, e.NewValue, StringComparison.Ordinal)
                ? e.OldValue
                : $"{e.OldValue} -> {e.NewValue}";

        private static string Cell(string value)
            => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r\n", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }
}
