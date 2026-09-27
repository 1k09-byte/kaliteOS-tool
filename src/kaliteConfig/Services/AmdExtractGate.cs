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

namespace kaliteConfig.Services
{
    /// <summary>
    /// Decides whether an AMD driver package was really unpacked, and if not,
    /// what to tell the user. Split out from <see cref="AmdDriverService"/> so
    /// the decision is testable without a 900 MB download and a 7-Zip install.
    /// </summary>
    public static class AmdExtractGate
    {
        /// <summary>
        /// 7-Zip exit codes: 0 = no error, 1 = warning (e.g. a file was locked),
        /// 2 = fatal error, 7 = command line error, 8 = not enough memory, ...
        /// A warning still leaves a usable tree on disk, so anything up to and
        /// including 1 is allowed to continue to the manifest check.
        /// </summary>
        public const int MaxAcceptableExitCode = 1;

        public static bool IsAcceptableExitCode(int exitCode) => exitCode <= MaxAcceptableExitCode;

        /// <summary>
        /// The manifest is the real success test. 7-Zip can exit 0 on an archive
        /// that simply did not contain the driver payload - for example when a
        /// reduced build that cannot open PE self-extractors was used.
        /// </summary>
        public const string ManifestRelativePath = @"Bin64\cccmanifest_64.json";

        public static bool IsSuccess(int exitCode, bool manifestPresent)
            => IsAcceptableExitCode(exitCode) && manifestPresent;

        /// <summary>
        /// User-facing reason for a failed extraction, or null when it succeeded.
        /// </summary>
        public static string? FailureMessage(int exitCode, bool manifestPresent)
        {
            if (IsSuccess(exitCode, manifestPresent)) return null;

            if (!IsAcceptableExitCode(exitCode))
                return $"7-Zip could not unpack the AMD package (exit code {exitCode}).";

            return $"The AMD package unpacked, but it did not contain {ManifestRelativePath}, so there is nothing to install.";
        }

        /// <summary>
        /// True when `7z i` printed a format-table row for PE, e.g.
        ///   ".........E............  PE       exe dll sys   M Z"
        /// Only the full 7-Zip build has one; the reduced 7za.exe / 7zr.exe builds
        /// stop at 7z / zip / tar / xz / cab and cannot open a PE self-extractor -
        /// which is why the AMD package used to fail no matter what it downloaded.
        /// </summary>
        public static bool ListsPeFormat(string sevenZipInfoOutput)
        {
            if (string.IsNullOrEmpty(sevenZipInfoOutput)) return false;

            foreach (string line in sevenZipInfoOutput.Split('\n'))
            {
                string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i + 1 < tokens.Length; i++)
                {
                    if (tokens[i] == "PE" && tokens[i + 1].StartsWith("exe", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// A driver package is a Windows executable, so it must start with "MZ".
        /// AMD's CDN serves a "Download Not Complete" HTML page with HTTP 200 when
        /// it decides an automated request is unwelcome, which is why the payload
        /// has to be sniffed rather than trusted because the status code was 200.
        /// </summary>
        public static bool IsPeExecutable(ReadOnlySpan<byte> magic)
            => magic.Length >= 2 && magic[0] == (byte)'M' && magic[1] == (byte)'Z';

        /// <summary>
        /// Wording for "the server answered, but not with a driver".
        /// </summary>
        /// <param name="declaredLength">Content-Length, or null/0 if the server did not say.</param>
        /// <param name="preview">Optional short excerpt of what actually arrived, prefixed with ": ".</param>
        public static string UnexpectedPayloadMessage(long? declaredLength, string? preview)
        {
            string size = declaredLength is > 0
                ? $"{declaredLength.Value:N0} bytes"
                : "an unknown number of bytes";

            string excerpt = string.IsNullOrWhiteSpace(preview) ? string.Empty : preview.Trim();

            return $"AMD's server returned a web page ({size}{excerpt}) instead of the driver package. " +
                   "This is usually AMD's CDN refusing an automated request - try again in a few minutes, " +
                   "or download the package from amd.com and install it directly.";
        }

        /// <summary>
        /// An Adrenalin package is LZMA2-compressed: the 26.9.1 release measured
        /// 930,590,688 bytes on the wire and 2,679,509,920 bytes unpacked, a ratio
        /// of 2.88. Round up so the estimate holds for other packages too.
        ///
        /// The app no longer unpacks anything itself - it starts AMD's installer,
        /// which does. But that installer unpacks the same payload into a
        /// temporary folder on the same volume, so the space has to exist before
        /// the user commits to a 900 MB download.
        /// </summary>
        public const double ExpansionRatio = 3.2;

        /// <summary>Slack on top of the estimate, so a "just fits" answer still fails politely.</summary>
        public const long HeadroomBytes = 512L * 1024 * 1024;

        /// <summary>
        /// How much room the unpacked driver tree needs. The compressed package is
        /// still on disk while this is being written, so the two add up.
        /// </summary>
        public static long EstimateUnpackedBytes(long downloadBytes)
            => downloadBytes <= 0 ? 0 : (long)(downloadBytes * ExpansionRatio);

        /// <summary>
        /// Free space needed on the temp volume to hold the download *and* the
        /// unpacked driver. Downloading first and discovering there is no room
        /// afterwards wastes a gigabyte of the user's bandwidth to fail.
        /// </summary>
        public static long RequiredFreeBytes(long downloadBytes)
        {
            if (downloadBytes <= 0) return HeadroomBytes;
            return downloadBytes + EstimateUnpackedBytes(downloadBytes) + HeadroomBytes;
        }

        public static bool HasRoomFor(long freeBytes, long downloadBytes)
            => freeBytes >= RequiredFreeBytes(downloadBytes);

        public static string InsufficientSpaceMessage(long downloadBytes, long freeBytes)
        {
            long need = RequiredFreeBytes(downloadBytes);
            long shortfall = Math.Max(0, need - freeBytes);
            return $"Not enough free disk space to install this driver. The {Format(downloadBytes)} download unpacks to " +
                   $"roughly {Format(EstimateUnpackedBytes(downloadBytes))}, so about {Format(need)} has to be free at once " +
                   $"(including a {Format(HeadroomBytes)} safety margin). Only {Format(freeBytes)} is available - " +
                   $"free up {Format(shortfall)} more and press Retry.";
        }

        /// <summary>What to do with a partial file once the server's answer is known.</summary>
        public enum ResumeDecision
        {
            /// <summary>The body is exactly the missing tail - append it.</summary>
            Append,

            /// <summary>The body is the whole file - the range was ignored, write it fresh.</summary>
            Replace,

            /// <summary>The body is neither - the partial belongs to something else. Drop both.</summary>
            Discard,
        }

        /// <summary>
        /// Decides what a resuming response body means.
        ///
        /// AMD's CDN answers "Range: bytes=N-" with HTTP 200, no Content-Range, and
        /// a body trimmed to the offset, so the response never says which part of
        /// the file arrived. The only way to tell is arithmetic against the size
        /// recorded when the transfer started:
        ///
        ///   body == total - have   the tail we asked for      -> append
        ///   body == total          the range was ignored      -> start over
        ///   anything else          a different driver entirely -> drop both
        ///
        /// Getting this wrong is not a cosmetic bug: stitching a fresh download
        /// onto a stale partial yields a file of exactly the right length whose
        /// middle is nonsense, and it only fails when something tries to run it.
        /// </summary>
        public static ResumeDecision DecideResume(long expectedTotal, long alreadyHave, long bodyLength)
        {
            if (expectedTotal <= 0 || alreadyHave <= 0) return ResumeDecision.Replace;
            if (alreadyHave >= expectedTotal) return ResumeDecision.Discard;
            if (bodyLength == expectedTotal - alreadyHave) return ResumeDecision.Append;
            if (bodyLength == expectedTotal) return ResumeDecision.Replace;
            return ResumeDecision.Discard;
        }

        private static string Format(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
            return $"{bytes:N0} bytes";
        }
    }
}
