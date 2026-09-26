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
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace kaliteConfig.Services
{
    public class JournalEntry
    {
        public string Component { get; set; } = string.Empty;
        public string PathOrKey { get; set; } = string.Empty;
        public string ValueBefore { get; set; } = string.Empty;
        public string ValueAfter { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class RollbackJournalService
    {
        private static readonly string JournalFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "kaliteConfig", "Journal", "rollback_journal.json");
        private readonly List<JournalEntry> _journal;

        public RollbackJournalService()
        {
            var directory = Path.GetDirectoryName(JournalFilePath);
            if (!Directory.Exists(directory) && directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            _journal = LoadJournal();
        }

        public async Task LogChangeAsync(string component, string pathOrKey, string valueBefore, string valueAfter)
        {
            var entry = new JournalEntry
            {
                Component = component,
                PathOrKey = pathOrKey,
                ValueBefore = valueBefore,
                ValueAfter = valueAfter,
                Timestamp = DateTime.UtcNow
            };

            _journal.Add(entry);
            await SaveJournalAsync();
        }

        private List<JournalEntry> LoadJournal()
        {
            try
            {
                if (File.Exists(JournalFilePath))
                {
                    string json = File.ReadAllText(JournalFilePath);
                    return JsonSerializer.Deserialize<List<JournalEntry>>(json) ?? new List<JournalEntry>();
                }
            }
            catch
            {
                // Ignore load errors, start fresh
            }
            return new List<JournalEntry>();
        }

        private async Task SaveJournalAsync()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_journal, options);
                await File.WriteAllTextAsync(JournalFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write journal: {ex.Message}");
            }
        }
    }
}
