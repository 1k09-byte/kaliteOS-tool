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
        /// <summary>
        /// Human-readable reason the last <see cref="ExtractInstallerAsync"/> call
        /// returned false. The GPU drivers page shows this instead of a bare
        /// "AMD Extraction failed." so the user knows what to actually do.
        /// </summary>
        public string? LastExtractError { get; private set; }

        private static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "Logs", "amd-driver.log");

        /// <summary>
        /// Mirrors a progress line to the on-screen status text *and* to a log
        /// file. Extraction used to report straight into Debug output, which made
        /// every failure undiagnosable from the outside.
        /// </summary>
        private static void Log(Action<string> logCallback, string message)
        {
            Debug.WriteLine(message);
            try
            {
                string path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch { /* logging must never break an install */ }
            logCallback(message);
        }

        /// <summary>
        /// Every place a full 7-Zip might live, best candidate first.
        /// </summary>
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

            // Last resort: whatever 7z.exe is on PATH.
            yield return "7z.exe";
        }

        /// <summary>
        /// AMD ships its drivers as a self-extracting .exe (a PE stub with a 7z
        /// payload glued onto it). Only the *full* 7-Zip build has the PE archive
        /// handler; the reduced builds - 7zr.exe from the LZMA SDK and 7za.exe
        /// from the "extra" package - list only 7z/zip/tar/xz/cab and bail out
        /// with nothing but "ERROR: file". That mismatch is what made every AMD
        /// package report "AMD Extraction failed." regardless of the download.
        ///
        /// Ask the binary itself rather than trusting its name: someone (us, once)
        /// may have saved a reduced build under the full build's file name.
        /// </summary>
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

        /// <summary>
        /// Finds a 7-Zip that can actually open a PE self-extractor, or null.
        /// </summary>
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
                string zExe = ResolveFullSevenZip(logCallback, ct);
                if (zExe is null)
                {
                    LastExtractError = "7-Zip could not be found. The AMD driver is a self-extracting .exe, which only the full 7-Zip build can open - install it from https://www.7-zip.org/ and press Retry.";
                    Log(logCallback, LastExtractError);
                    return false;
                }

                // A previous attempt may have died half way through; start clean so
                // the manifest check below cannot be fooled by stale leftovers.
                ResetDirectory(extractDir);

                // 7z.exe x "<installer.exe>" -o"<extractDir>" -y -aoa
                //   -y   answer yes to every prompt (we are non-interactive)
                //   -aoa overwrite all existing files without asking again
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

                // Drain both pipes before awaiting, or a chatty 7-Zip can fill the
                // stderr buffer and deadlock against our own wait.
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

                // The exit code is not the real success test - the manifest is.
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

        /// <summary>
        /// Empties a directory we own (the app's own temp extraction folder).
        /// Best effort - a file held open by something else is simply left alone.
        /// </summary>
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
            catch (Exception ex)
            {
                Debug.WriteLine($"ResetDirectory failed: {ex.Message}");
            }
        }

        /// <summary>Prefix of the per-attempt installer files in the temp folder.</summary>
        private const string InstallerFilePrefix = "amd_software_installer_";

        /// <summary>
        /// Hands the downloaded package to the user by starting it.
        ///
        /// An AMD Adrenalin package is a self-extracting installer: running it
        /// unpacks the payload and drives the install itself. The app used to
        /// unpack the same 2.7 GB by hand, parse cccmanifest_64.json, build a
        /// component list and drive Setup.exe - which cost 2.7 GB of %TEMP%,
        /// needed a 7-Zip that could read PE, and still ended up in AMD's
        /// installer anyway.
        /// </summary>
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

        /// <summary>
        /// Removes the downloaded package and the unpacked tree.
        ///
        /// This is not housekeeping, it is correctness: an Adrenalin package is
        /// ~930 MB on the wire and ~2.7 GB unpacked, and leaving both behind in
        /// %TEMP% after every attempt is how the temp volume fills up and the
        /// *next* attempt fails for lack of space. Best effort - a file held open
        /// by an antivirus scan is left for the next sweep.
        /// </summary>
        public void CleanupTempArtifacts(string installerPath, string extractDir, Action<string> logCallback)
        {
            long freed = 0;
            freed += DeleteFileQuietly(installerPath);
            freed += DeleteDirectoryQuietly(extractDir);
            if (freed > 0)
                Log(logCallback, $"Cleaned up {freed / (1024.0 * 1024 * 1024):0.##} GB of temporary driver files.");
        }

        /// <summary>
        /// Clears leftovers from earlier attempts before starting a new one, so a
        /// previous failure cannot eat the space the new attempt needs.
        /// </summary>
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
            catch (Exception ex)
            {
                Debug.WriteLine($"SweepStaleTempArtifacts failed: {ex.Message}");
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
