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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services
{
    /// <summary>
    /// Runs Radeon Software Slimmer, a third-party utility that strips the bloat
    /// out of an Adrenalin install (background services, scheduled tasks, the
    /// telemetry-adjacent extras).
    ///
    /// Deliberately *not* bundled: Radeon Software Slimmer is GPL-3.0, and this
    /// app is proprietary. Fetching the release from the author's GitHub at the
    /// user's request and starting it as a separate process keeps the two
    /// programs at arm's length - nothing here links against it or embeds its
    /// code. The licence and source link are shown in the UI alongside the button
    /// so the attribution travels with the use.
    ///
    /// It needs administrator rights, so the launch is a real UAC prompt.
    /// </summary>
    public static class RadeonSlimmerService
    {
        /// <summary>Where the tool's own page and source live.</summary>
        public const string ProjectUrl = "https://github.com/GSDragoon/RadeonSoftwareSlimmer";

        /// <summary>
        /// Pinned to a specific release rather than "latest", so a new upstream
        /// build cannot change behaviour under a user who has already approved
        /// the download once. Bump deliberately.
        /// </summary>
        private const string ReleaseTag = "1.12.0";

        /// <summary>
        /// The .NET Framework build. It needs no extra runtime: .NET Framework
        /// 4.8 ships with Windows 10 1903 and Windows 11. The net80 build would
        /// need a .NET Desktop Runtime the user may not have.
        /// </summary>
        private const string AssetUrl =
            "https://github.com/GSDragoon/RadeonSoftwareSlimmer/releases/download/" +
            ReleaseTag + "/RadeonSoftwareSlimmer_" + ReleaseTag + "_net48.zip";

        private const string ExeName = "RadeonSoftwareSlimmer.exe";

        private static readonly HttpClient Http = new();

        public static string InstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "Tools", "RadeonSoftwareSlimmer");

        public static string ExePath => Path.Combine(InstallDir, ExeName);

        public static bool IsInstalled => File.Exists(ExePath);

        /// <summary>Result of a launch attempt, so the UI can say what went wrong.</summary>
        public sealed class LaunchResult
        {
            public bool Success { get; init; }
            public string? Error { get; init; }
            public bool Cancelled { get; init; }
        }

        /// <summary>
        /// Makes sure the tool is unpacked, then starts it elevated.
        /// </summary>
        public static async Task<LaunchResult> LaunchAsync(Action<string> log, IProgress<double>? progress, CancellationToken ct)
        {
            try
            {
                if (!IsInstalled)
                {
                    log($"Downloading Radeon Software Slimmer {ReleaseTag}...");
                    await DownloadAsync(log, progress, ct);
                }

                log($"Starting {ExeName} (a Windows administrator prompt will appear).");
                return StartElevated();
            }
            catch (OperationCanceledException)
            {
                return new LaunchResult { Cancelled = true };
            }
            catch (Exception ex)
            {
                string message = $"Radeon Software Slimmer could not be started: {ex.Message}";
                log(message);
                return new LaunchResult { Error = message };
            }
        }

        /// <summary>
        /// Fetches the release zip and unpacks it. It is an ordinary zip, so
        /// System.IO.Compression is enough - no external unpacker needed.
        /// </summary>
        private static async Task DownloadAsync(Action<string> log, IProgress<double>? progress, CancellationToken ct)
        {
            Directory.CreateDirectory(InstallDir);

            // Download to a sibling first: a half-written folder here would look
            // installed on the next run and fail to start forever.
            string staging = InstallDir + ".partial";
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);

                string zipPath = Path.Combine(staging, "RadeonSoftwareSlimmer.zip");
                using (var response = await Http.GetAsync(AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? 0;
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await using (var destination = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        byte[] buffer = new byte[81920];
                        long written = 0;
                        int lastPercent = -1;
                        while (true)
                        {
                            int read = await source.ReadAsync(buffer, ct);
                            if (read <= 0) break;
                            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                            written += read;
                            if (total > 0)
                            {
                                int percent = (int)(written * 100 / total);
                                if (percent != lastPercent)
                                {
                                    lastPercent = percent;
                                    progress?.Report(percent);
                                }
                            }
                        }
                    }
                }

                ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

                if (!File.Exists(Path.Combine(staging, ExeName)))
                {
                    throw new InvalidOperationException(
                        $"the archive did not contain {ExeName} - the download may be an error page rather than the release");
                }

                // The archive has served its purpose. Leaving it inside the folder
                // we are about to install would park a few megabytes of dead weight
                // next to the tool for no benefit.
                TryDeleteFile(zipPath);

                // Swap the finished folder into place.
                if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);
                Directory.Move(staging, InstallDir);

                log($"Radeon Software Slimmer {ReleaseTag} is ready.");
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        private static LaunchResult StartElevated()
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                UseShellExecute = true,
                Verb = "runas", // the tool needs administrator rights
                WorkingDirectory = InstallDir,
            };

            try
            {
                Process.Start(psi);
                return new LaunchResult { Success = true };
            }
            catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
            {
                // 1223 = ERROR_CANCELLED - the user said no at the UAC prompt.
                // Not an error worth shouting about.
                return new LaunchResult { Cancelled = true };
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>Removes the downloaded copy, e.g. from a "clear cached tools" action.</summary>
        public static bool Remove()
        {
            try
            {
                if (!Directory.Exists(InstallDir)) return false;
                Directory.Delete(InstallDir, true);
                return true;
            }
            catch { return false; }
        }
    }
}
