using kaliteConfig.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

public sealed class DriverStoreService
{
    private const int EnumTimeoutMs = 60_000;
    private const int DeleteTimeoutMs = 120_000;

    public async Task<List<DriverPackageItem>> GetPackagesAsync(
        CancellationToken ct = default, IProgress<string>? progress = null)
    {
        try
        {
            progress?.Report("Listing driver store packages…");
            string output = await RunPnputilAsync("/enum-drivers", EnumTimeoutMs, ct).ConfigureAwait(false);
            var items = ParseEnumOutput(output);
            progress?.Report($"Measuring {items.Count} package(s)…");
            await Task.Run(() =>
            {
                foreach (var item in items)
                {
                    if (ct.IsCancellationRequested) break;
                    item.StoreSizeBytes = MeasureStoreSize(item.OriginalName);
                }
            }, ct);
            return items.OrderBy(i => i.Provider).ThenBy(i => i.DisplayName).ToList();
        }
        catch { return new List<DriverPackageItem>(); }
    }

    public async Task<string?> DeletePackageAsync(DriverPackageItem item, CancellationToken ct = default)
    {
        string inf = (item.PublishedName ?? "").Trim();
        if (inf.Length == 0 || !inf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
            return $"Refusing to delete: unexpected published name '{item.PublishedName}'.";
        string stem = Path.GetFileNameWithoutExtension(inf) ?? "";
        if (!stem.StartsWith("oem", StringComparison.OrdinalIgnoreCase)
            || !stem.Skip(3).All(char.IsDigit))
            return $"Refusing to delete: '{inf}' is not a third-party (oem##.inf) package.";
        try
        {
            _ = await RunPnputilAsync(
                $"/delete-driver \"{inf}\" /uninstall /force", DeleteTimeoutMs, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    internal static async Task<string> RunPnputilAsync(string arguments, int timeoutMs, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        if (!proc.Start()) throw new InvalidOperationException("Could not start pnputil.exe.");
        string verb = arguments.Split(' ')[0];
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"pnputil {verb} timed out after {timeoutMs / 1000}s.");
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr) ? $"pnputil exited with code {proc.ExitCode}." : stderr.Trim());
        return stdout;
    }

    internal static List<DriverPackageItem> ParseEnumOutput(string output)
    {
        var items = new List<DriverPackageItem>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (current.TryGetValue("Published Name", out string? published)
                && !string.IsNullOrWhiteSpace(published))
            {
                current.TryGetValue("Original Name", out string? original);
                current.TryGetValue("Provider Name", out string? provider);
                current.TryGetValue("Class Name", out string? className);
                current.TryGetValue("Signer Name", out string? signer);
                SplitDriverVersion(
                    current.TryGetValue("Driver Version", out string? dv) ? dv : null,
                    out string date, out string version);
                items.Add(new DriverPackageItem
                {
                    PublishedName = published.Trim(),
                    OriginalName = (original ?? "").Trim(),
                    Provider = (provider ?? "").Trim(),
                    ClassName = (className ?? "").Trim(),
                    DriverVersion = version,
                    DriverDate = date,
                    SignerName = (signer ?? "").Trim(),
                });
            }
            current.Clear();
        }

        foreach (string rawLine in (output ?? "").Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) { Flush(); continue; }
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line.Substring(0, colon).Trim();
            string value = line.Substring(colon + 1).Trim();
            if (key.Length == 0) continue;
            if (key.Equals("Published Name", StringComparison.OrdinalIgnoreCase) && current.Count > 0)
                Flush();
            current[key] = value;
        }
        Flush();
        return items;
    }

    internal static void SplitDriverVersion(string? raw, out string date, out string version)
    {
        date = "";
        version = (raw ?? "").Trim();
        if (version.Length == 0) return;
        int comma = version.IndexOf(',');
        if (comma >= 0)
        {
            date = version.Substring(0, comma).Trim();
            version = version.Substring(comma + 1).Trim();
            return;
        }
        var parts = version.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Contains('/'))
        {
            date = parts[0];
            version = parts[parts.Length - 1];
        }
    }

    internal static long MeasureStoreSize(string? originalName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(originalName)) return -1;
            string repo = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "DriverStore", "FileRepository");
            if (!Directory.Exists(repo)) return -1;
            string stem = Path.GetFileNameWithoutExtension(originalName.Trim()) + "_";
            long total = 0;
            bool any = false;
            foreach (string dir in Directory.EnumerateDirectories(repo, stem + "*"))
            {
                any = true;
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; } catch { }
                }
            }
            return any ? total : -1;
        }
        catch { return -1; }
    }
}
