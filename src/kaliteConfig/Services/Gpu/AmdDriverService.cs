using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services
{
    public class AmdDriverService
    {
        public string? LastExtractError { get; private set; }

        private static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "Logs", "amd-driver.log");

        private static void Log(Action<string> logCallback, string message)
        {
            try
            {
                string path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch {  }
            logCallback(message);
        }

        private static IEnumerable<string> SevenZipCandidates()
        {
            string toolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "Tools");
            yield return Path.Combine(toolsDir, "7z.exe");

            foreach (string root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            })
            {
                if (!string.IsNullOrEmpty(root))
                    yield return Path.Combine(root, "7-Zip", "7z.exe");
            }

            yield return "7z.exe";
        }

        private static bool CanOpenPeArchives(string exePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "i",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process is null) return false;

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10_000))
                {
                    try { process.Kill(); } catch { }
                    return false;
                }
                string stdout = stdoutTask.GetAwaiter().GetResult();

                return AmdExtractGate.ListsPeFormat(stdout);
            }
            catch
            {
                return false;
            }
        }

        private static string? ResolveFullSevenZip(Action<string> logCallback, CancellationToken ct)
        {
            foreach (string candidate in SevenZipCandidates())
            {
                ct.ThrowIfCancellationRequested();
                if (CanOpenPeArchives(candidate))
                {
                    Log(logCallback, $"7-Zip: using {candidate}");
                    return candidate;
                }
                Log(logCallback, $"7-Zip: {candidate} is missing, broken, or a reduced build that cannot open .exe packages.");
            }

            Log(logCallback, "7-Zip: no full build found.");
            return null;
        }

        public async Task<bool> ExtractInstallerAsync(string installerExePath, string extractDir, Action<string> logCallback, CancellationToken ct)
        {
            LastExtractError = null;
            try
            {
                string? zExe = ResolveFullSevenZip(logCallback, ct);
                if (zExe is null)
                {
                    LastExtractError = "7-Zip could not be found. The AMD driver is a self-extracting .exe, which only the full 7-Zip build can open - install it from https://www.7-zip.org/ and press Retry.";
                    Log(logCallback, LastExtractError);
                    return false;
                }

                ResetDirectory(extractDir);

                string args = $"x \"{installerExePath}\" -o\"{extractDir}\" -y -aoa";
                Log(logCallback, $"Extracting AMD package: \"{zExe}\" {args}");

                var psi = new ProcessStartInfo
                {
                    FileName = zExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process is null)
                {
                    LastExtractError = $"Could not start 7-Zip ({zExe}).";
                    Log(logCallback, LastExtractError);
                    return false;
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(ct);
                string stdout = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();

                Log(logCallback, $"7-Zip exited with code {process.ExitCode}.");
                foreach (string line in stdout.Split('\n').TakeLast(8))
                {
                    if (!string.IsNullOrWhiteSpace(line)) Log(logCallback, "  7z> " + line.Trim());
                }
                if (!string.IsNullOrWhiteSpace(stderr))
                    Log(logCallback, "  7z! " + stderr.Trim().Replace("\n", " | "));

                string manifestPath = Path.Combine(extractDir, AmdExtractGate.ManifestRelativePath);
                bool manifestPresent = File.Exists(manifestPath);

                if (!AmdExtractGate.IsSuccess(process.ExitCode, manifestPresent))
                {
                    LastExtractError = AmdExtractGate.FailureMessage(process.ExitCode, manifestPresent)
                                       + $" See {LogPath}.";
                    Log(logCallback, LastExtractError);
                    if (manifestPresent == false && Directory.Exists(extractDir))
                    {
                        string top = string.Join(", ", Directory
                            .EnumerateFileSystemEntries(extractDir)
                            .Take(12)
                            .Select(Path.GetFileName));
                        Log(logCallback, $"  Unpacked top level: {top}");
                    }
                    return false;
                }

                Log(logCallback, $"Found {AmdExtractGate.ManifestRelativePath}.");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastExtractError = $"Extraction failed: {ex.Message}";
                Log(logCallback, LastExtractError);
                return false;
            }
        }

        private static void ResetDirectory(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    return;
                }
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    try
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, true);
                        else File.Delete(entry);
                    }
                    catch { }
                }
            }
            catch (Exception)
            {
            }
        }

        private const string InstallerFilePrefix = "amd_software_installer_";

        public bool LaunchInstaller(string installerPath, Action<string> logCallback)
        {
            if (!File.Exists(installerPath))
            {
                Log(logCallback, $"Installer is missing at {installerPath}.");
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true,
                });
                Log(logCallback, $"Started {Path.GetFileName(installerPath)}.");
                return true;
            }
            catch (Exception ex)
            {
                Log(logCallback, $"Could not start the installer: {ex.Message}");
                return false;
            }
        }

        public void CleanupTempArtifacts(string installerPath, string extractDir, Action<string> logCallback)
        {
            long freed = 0;
            freed += DeleteFileQuietly(installerPath);
            freed += DeleteDirectoryQuietly(extractDir);
            if (freed > 0)
                Log(logCallback, $"Cleaned up {freed / (1024.0 * 1024 * 1024):0.##} GB of temporary driver files.");
        }

        public void SweepStaleTempArtifacts(Action<string> logCallback)
        {
            string temp = Path.GetTempPath();
            long freed = 0;
            freed += DeleteDirectoryQuietly(Path.Combine(temp, "AMD_Extract"));

            try
            {
                foreach (string file in Directory.EnumerateFiles(temp, InstallerFilePrefix + "*.exe"))
                    freed += DeleteFileQuietly(file);
            }
            catch (Exception)
            {
            }

            if (freed > 0)
                Log(logCallback, $"Freed {freed / (1024.0 * 1024 * 1024):0.##} GB left behind by an earlier attempt.");
        }

        private static long DeleteFileQuietly(string path)
        {
            try
            {
                if (!File.Exists(path)) return 0;
                long size = new FileInfo(path).Length;
                File.Delete(path);
                return size;
            }
            catch { return 0; }
        }

        private static long DeleteDirectoryQuietly(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return 0;
                long size = DirectorySize(dir);
                Directory.Delete(dir, true);
                return size;
            }
            catch { return 0; }
        }

        private static long DirectorySize(string dir)
        {
            try
            {
                long total = 0;
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; } catch { }
                }
                return total;
            }
            catch { return 0; }
        }

    }
}
