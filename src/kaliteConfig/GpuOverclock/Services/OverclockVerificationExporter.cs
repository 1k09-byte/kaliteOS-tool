// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
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
    /// <summary>
    /// Maps the change-log viewer's ComboBox filter indexes to the enum filters
    /// (index 0 = "all", 1..n = enum declaration order). Kept here so the
    /// mapping is pure logic - unit-testable without the WinUI ViewModel.
    /// </summary>
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

    /// <summary>
    /// Builds the GPU overclock verification record (Markdown) from the change
    /// log: a human-readable audit document in the same shape as
    /// Docs/OverclockVerification.md, exportable from the in-app viewer.
    /// Pure string building - no filesystem, no UI - so it round-trips in tests.
    /// </summary>
    public static class OverclockVerificationExporter
    {
        /// <summary>Recommended file name for a record generated at <paramref name="when"/>.</summary>
        public static string SuggestedFileName(DateTime when)
            => $"kaliteConfig-overclock-verification-{when:yyyy-MM-dd}.md";

        /// <summary>
        /// Builds the full record. Entries appear oldest-first (chronological,
        /// like the on-disk log), one table row per logged change.
        /// </summary>
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

            // ---- summary counts ----
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| Result | Count |");
            sb.AppendLine("|---|---|");
            foreach (OverclockChangeResult r in Enum.GetValues(typeof(OverclockChangeResult)))
                sb.Append("| ").Append(r).Append(" | ")
                  .Append(entries.Count(e => e.Result == r)).Append(" |");
            sb.AppendLine();
            sb.AppendLine();

            // ---- per-entry table ----
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

        /// <summary>Table cells never break the table: escape pipes and newlines.</summary>
        private static string Cell(string value)
            => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r\n", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }
}
