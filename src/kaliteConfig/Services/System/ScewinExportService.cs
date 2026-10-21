using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

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

    public static bool HasCachedDump => File.Exists(DumpPath);

    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
    {
        var exePath = Path.Combine(ToolDirectory, ExeName);
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException(
                $"SCEWIN_64.exe was not found next to the app (expected {exePath}).");
        }

        try { File.Delete(DumpPath); } catch {  }

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

    public async Task ImportAsync(string pathToFile, CancellationToken cancellationToken = default)
    {
        var exePath = Path.Combine(ToolDirectory, ExeName);
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"SCEWIN_64.exe was not found next to the app.");
        }

        var working = Path.Combine(Path.GetDirectoryName(DumpPath)!, ToolDirName);
        Directory.CreateDirectory(working);

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

        if (process.ExitCode != 0 && process.ExitCode != 4)
        {
            throw new InvalidOperationException($"SCEWIN_64.exe exited with code {process.ExitCode} during import.");
        }
    }
}
