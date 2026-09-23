using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.PackageManager.Models;

namespace kaliteConfig.PackageManager.Services;

/// <summary>
/// WinGet backend over winget.exe CLI.
///
/// WHY CLI, NOT COM: Microsoft.Management.Deployment (the WinGet COM API)
/// does not resolve in this app's dependency context (verified: CS0234 with
/// and without the WindowsAppSDK reference - only Microsoft.Windows.* MSIX
/// projections ship). winget.exe v1.29 here also offers no --output json, so
/// this backend parses the human-readable tables with dash-anchored column
/// slicing (never whitespace-splitting, so names/ids with spaces survive).
/// That makes this parser the fragile heart of the backend: it is isolated
/// in pure static methods covered by tools/PackageVerify, and unknown lines
/// are skipped loudly (rows dropped) rather than misread. A future winget
/// with stable JSON output should replace ParsePackageTable, not patch it.
///
/// Verified live against winget v1.29.290 (list/upgrade/search/install flags).
/// </summary>
public sealed class WinGetPackageSource : IPackageSource
{
    public string SourceId => PackageSourceIds.WinGet;
    public string DisplayName => "WinGet";
    public string Description => "Windows Package Manager: community + Microsoft Store catalogs.";

    public bool CanSearch => true;
    public bool CanInstall => true;
    public bool CanUpdate => true;
    public bool CanUninstall => true;
    public bool CanListInstalled => true;
    public bool CanListUpdates => true;

    /// <summary>Optional executable override (preferences page) - null means PATH lookup.</summary>
    public string? ExecutableOverride { get; set; }

    private string Exe => string.IsNullOrWhiteSpace(ExecutableOverride) ? "winget" : ExecutableOverride!.Trim();

    private const int ListTimeoutMs = 60_000;
    private const int OperationTimeoutMs = 30 * 60_000;

    public async Task<PackageSourceStatus> DetectAsync(CancellationToken ct = default)
    {
        var status = new PackageSourceStatus
        {
            SourceId = SourceId, DisplayName = DisplayName, Description = Description,
            IsImplemented = true, IsEnabled = true,
        };
        try
        {
            var result = await CliProcess.RunAsync(Exe, "--version", 10_000, ct).ConfigureAwait(false);
            if (result.ExitCode != 0) return status;
            status.IsDetected = true;
            status.DetectedVersion = result.Stdout.Trim().TrimStart('v', 'V').Split('\n')[0].Trim();
            status.ExecutablePath = await LocateExecutableAsync(ct).ConfigureAwait(false);
        }
        catch { /* never throws: undetected is a state, not an error */ }
        return status;
    }

    private async Task<string> LocateExecutableAsync(CancellationToken ct)
    {
        try
        {
            var result = await CliProcess.RunAsync("where", "winget", 10_000, ct).ConfigureAwait(false);
            if (result.ExitCode != 0) return "";
            foreach (string line in result.Stdout.Split('\n'))
            {
                string path = line.Trim().TrimEnd('\r');
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return path;
            }
        }
        catch { }
        return "";
    }

    public async Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query, PackageSearchMode mode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<PackageInfo>();
        string args = mode switch
        {
            PackageSearchMode.Name => $"search --name \"{query}\" --disable-interactivity --accept-source-agreements",
            PackageSearchMode.Id => $"search --id \"{query}\" --disable-interactivity --accept-source-agreements",
            PackageSearchMode.Exact => $"search --query \"{query}\" --exact --disable-interactivity --accept-source-agreements",
            _ => $"search --query \"{query}\" --disable-interactivity --accept-source-agreements",
        };
        var result = await CliProcess.RunAsync(Exe, args, ListTimeoutMs, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(FirstErrorLine(result));
        var items = ParsePackageTable(result.Stdout, PackageSourceIds.WinGet, "WinGet");
        if (mode == PackageSearchMode.Similar && items.Count > 1)
        {
            // Winget's own ranking first; exact-substring hits float above
            // merely similar ones, client-side and transparent.
            string q = query.Trim();
            items = items
                .OrderByDescending(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || i.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        return items;
    }

    public async Task<IReadOnlyList<PackageInfo>> GetInstalledAsync(CancellationToken ct = default)
    {
        var result = await CliProcess.RunAsync(Exe, "list --disable-interactivity --accept-source-agreements", ListTimeoutMs, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(FirstErrorLine(result));
        return ParsePackageTable(result.Stdout, PackageSourceIds.WinGet, "WinGet");
    }

    public async Task<IReadOnlyList<PackageInfo>> GetAvailableUpdatesAsync(CancellationToken ct = default)
    {
        var result = await CliProcess.RunAsync(Exe, "upgrade --disable-interactivity --accept-source-agreements", ListTimeoutMs, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(FirstErrorLine(result));
        return ParsePackageTable(result.Stdout, PackageSourceIds.WinGet, "WinGet");
    }

    public Task<PackageOperationResult> InstallAsync(
        PackageInfo package, string? scope,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        string scopeArg = string.Equals(scope, "machine", StringComparison.OrdinalIgnoreCase) ? " --scope machine"
            : string.Equals(scope, "user", StringComparison.OrdinalIgnoreCase) ? " --scope user" : "";
        return RunOperationAsync(
            $"install --id \"{package.Id}\" --exact --silent{scopeArg} --accept-package-agreements --accept-source-agreements --disable-interactivity",
            $"Installing {package.DisplayName}…", progress, ct);
    }

    public Task<PackageOperationResult> UpdateAsync(
        PackageInfo package,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        return RunOperationAsync(
            $"upgrade --id \"{package.Id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            $"Updating {package.DisplayName}…", progress, ct);
    }

    public Task<PackageOperationResult> UninstallAsync(
        PackageInfo package,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        return RunOperationAsync(
            $"uninstall --id \"{package.Id}\" --exact --silent --disable-interactivity",
            $"Uninstalling {package.DisplayName}…", progress, ct);
    }

    private async Task<PackageOperationResult> RunOperationAsync(
        string arguments, string phase,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new PackageOperationProgress { Message = phase });
        try
        {
            var lines = new Progress<string>(line =>
            {
                string tail = line.Trim();
                if (tail.Length > 0)
                    progress?.Report(new PackageOperationProgress { Message = tail });
            });
            var result = await CliProcess.RunAsync(Exe, arguments, OperationTimeoutMs, ct, lines).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return PackageOperationResult.Fail(FirstErrorLine(result));
            return PackageOperationResult.Ok("Done.");
        }
        catch (OperationCanceledException)
        {
            return PackageOperationResult.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            return PackageOperationResult.Fail(ex.Message);
        }
    }

    private static string FirstErrorLine(CliProcess.CliResult result)
    {
        foreach (string line in (result.Stderr + "\n" + result.Stdout).Split('\n'))
        {
            string t = line.Trim().TrimEnd('\r');
            if (t.Length == 0 || t.StartsWith("Windows Package Manager", StringComparison.Ordinal)
                || t.StartsWith("©", StringComparison.Ordinal)) continue;
            return t;
        }
        return $"winget exited with code {result.ExitCode}.";
    }

    /// <summary>
    /// Parses winget list/search/upgrade tables via dash-anchored column
    /// slicing: the dashes line gives exact column starts, so values with
    /// spaces (names, monikers) survive. Header names map columns (upgrade
    /// adds Available); unknown columns are ignored. Preamble, footers
    /// ("No … found", "N upgrades available.") and id-less rows are skipped.
    /// Pure logic - unit-tested against captured live output.
    /// </summary>
    internal static List<PackageInfo> ParsePackageTable(string output, string sourceId, string sourceLabel)
    {
        var items = new List<PackageInfo>();
        string[] lines = (output ?? "").Split('\n');
        int header = -1, dashes = -1;
        for (int i = 0; i < lines.Length - 1; i++)
        {
            string h = lines[i].TrimEnd('\r');
            string d = lines[i + 1].TrimEnd('\r').Trim();
            if (h.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0
                && h.IndexOf("Id", StringComparison.OrdinalIgnoreCase) >= 0
                && d.Length > 0 && d.Trim('-', ' ').Length == 0 && d.Contains('-'))
            {
                header = i;
                dashes = i + 1;
                break;
            }
        }
        if (header < 0) return items;

        string headerLine = lines[header].TrimEnd('\r');
        string dashLine = lines[dashes].TrimEnd('\r');
        var starts = new List<int>();
        foreach (Match m in Regex.Matches(dashLine, "-+"))
            starts.Add(m.Index);
        if (starts.Count < 2)
        {
            // Compact shape (exact-id queries emit ONE dash run instead of
            // per-column groups): fall back to the header's own 2+-space
            // gaps. Winget still pads values to those widths, so slicing
            // stays exact and spaces inside values survive. Verified live:
            // `winget search --id 7zip.7zip --exact` prints 31 dashes.
            foreach (Match m in Regex.Matches(headerLine, @"\S+").Cast<Match>().Skip(1))
                starts.Add(m.Index);
        }
        if (starts.Count == 0) return items;

        string Cell(string line, int col)
        {
            int from = starts[col];
            if (from >= line.Length) return "";
            int to = col + 1 < starts.Count ? Math.Min(starts[col + 1], line.Length) : line.Length;
            return to <= from ? "" : line.Substring(from, to - from).Trim();
        }

        var headerCells = new List<string>();
        for (int c = 0; c < starts.Count; c++)
            headerCells.Add(Cell(headerLine, c).ToLowerInvariant());
        int nameCol = headerCells.IndexOf("name");
        int idCol = headerCells.IndexOf("id");
        if (nameCol < 0 || idCol < 0) return items;
        int versionCol = headerCells.IndexOf("version");
        int availableCol = headerCells.IndexOf("available");
        int sourceCol = headerCells.IndexOf("source");
        int publisherCol = headerCells.IndexOf("publisher");

        for (int i = dashes + 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            string trimmed = line.Trim();
            if (trimmed.StartsWith("No ", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("upgrades available.", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("cannot be determined", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("include-unknown", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Windows Package Manager", StringComparison.Ordinal)
                || trimmed.StartsWith("©", StringComparison.Ordinal)) continue;

            string id = Cell(line, idCol);
            if (id.Length == 0) continue;
            items.Add(new PackageInfo
            {
                Id = id,
                Name = nameCol >= 0 ? Cell(line, nameCol) : id,
                SourceId = sourceId,
                SourceLabel = sourceCol >= 0 && Cell(line, sourceCol).Length > 0 ? Cell(line, sourceCol) : sourceLabel,
                Publisher = publisherCol >= 0 ? Cell(line, publisherCol) : "",
                InstalledVersion = versionCol >= 0 ? Cell(line, versionCol) : "",
                AvailableVersion = availableCol >= 0 ? Cell(line, availableCol) : "",
            });
        }
        return items;
    }
}
