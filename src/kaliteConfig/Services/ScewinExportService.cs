using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

/// <summary>
/// Runs the bundled SCEWIN_64.exe (Assets\scewin, deployed next to the app)
/// to produce a live BIOS settings dump, then parses it with
/// <see cref="ScewinParser"/>. The dump lands in %LOCALAPPDATA%\kaliteConfig.
/// Requires an elevated process (the app manifest already requests it).
/// </summary>
public sealed class ScewinExportService
{
    private const string ToolDirName = "scewin";
    private const string ExeName = "SCEWIN_64.exe";

    public static string ToolDirectory =>
        Path.Combine(AppContext.BaseDirectory, ToolDirName);

    public static string DumpPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kaliteConfig");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "BIOSSettings.txt");
        }
    }

    public static bool IsAvailable => File.Exists(Path.Combine(ToolDirectory, ExeName));

    /// <summary>True when a previously exported dump exists on disk.</summary>
    public static bool HasCachedDump => File.Exists(DumpPath);

    /// <summary>
    /// Exports the BIOS settings with SCEWIN_64.exe /O /S and returns the dump
    /// path. The tool must run with its own directory as CWD (it loads its
    /// driver .sys files from there), so the process is spawned with the tool
    /// directory as working directory. Throws on non-zero exit or a missing
    /// output file.
    /// </summary>
    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
    {
        var exePath = Path.Combine(ToolDirectory, ExeName);
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException(
                $"SCEWIN_64.exe was not found next to the app (expected {exePath}).");
        }

        // Older dump must not survive a failed/timed-out run.
        try { File.Delete(DumpPath); } catch { /* nothing to delete */ }

        // SCEWIN writes BIOSSettings.txt into its working directory and also
        // produces Dupes.txt; give it its own scratch folder for those.
        var working = Path.Combine(
            Path.GetDirectoryName(DumpPath)!, ToolDirName);
        Directory.CreateDirectory(working);
        try { File.Delete(Path.Combine(working, "BIOSSettings.txt")); } catch { }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "/O /S BIOSSettings.txt /SD Dupes.txt",
            WorkingDirectory = working,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start SCEWIN_64.exe.");
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"SCEWIN_64.exe exited with code {process.ExitCode}.");
        }

        var produced = Path.Combine(working, "BIOSSettings.txt");
        if (!File.Exists(produced))
        {
            throw new InvalidOperationException(
                "SCEWIN_64.exe ran but produced no BIOSSettings.txt.");
        }

        File.Copy(produced, DumpPath, overwrite: true);
        return DumpPath;
    }

    /// <summary>
    /// Applies a text-format settings file directly to the BIOS using SCEWIN_64.exe /I /S.
    /// Re-uses the scratch directory to avoid dumping logs in the source file's directory.
    /// </summary>
    public async Task ImportAsync(string pathToFile, CancellationToken cancellationToken = default)
    {
        var exePath = Path.Combine(ToolDirectory, ExeName);
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"SCEWIN_64.exe was not found next to the app.");
        }

        var working = Path.Combine(Path.GetDirectoryName(DumpPath)!, ToolDirName);
        Directory.CreateDirectory(working);

        // Copy the target file to the working directory for safe execution
        var targetFile = Path.Combine(working, "BIOS_Import.txt");
        File.Copy(pathToFile, targetFile, overwrite: true);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"/I /S BIOS_Import.txt",
            WorkingDirectory = working,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start SCEWIN_64.exe for import.");
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0 && process.ExitCode != 4) // Exit code 4 means success with warnings (e.g., read-only skip)
        {
            throw new InvalidOperationException($"SCEWIN_64.exe exited with code {process.ExitCode} during import.");
        }
    }
}
