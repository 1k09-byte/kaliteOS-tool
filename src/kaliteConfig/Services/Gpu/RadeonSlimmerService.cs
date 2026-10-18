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
    public static class RadeonSlimmerService
    {
        public const string ProjectUrl = "https://github.com/GSDragoon/RadeonSoftwareSlimmer";

        private const string ReleaseTag = "1.12.0";

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

        public sealed class LaunchResult
        {
            public bool Success { get; init; }
            public string? Error { get; init; }
            public bool Cancelled { get; init; }
        }

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

        private static async Task DownloadAsync(Action<string> log, IProgress<double>? progress, CancellationToken ct)
        {
            Directory.CreateDirectory(InstallDir);

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

                TryDeleteFile(zipPath);

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
                Verb = "runas",
                WorkingDirectory = InstallDir,
            };

            try
            {
                Process.Start(psi);
                return new LaunchResult { Success = true };
            }
            catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
            {
                return new LaunchResult { Cancelled = true };
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

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
